using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Kometra.Models.Astrometry;
using Kometra.Models.Astrometry.Solving;
using Kometra.Models.Fits;
using Kometra.Models.Fits.Structure;
using Kometra.Services.Fits;
using Kometra.Services.Fits.Metadata;

namespace Kometra.Services.Astrometry;

public class PlateSolvingService : IPlateSolvingService
{
    private readonly IFitsDataManager _dataManager; 
    private readonly IFitsMetadataService _metadataService;
    private readonly Lazy<string?> _executablePath;

    public PlateSolvingService(IFitsDataManager dataManager, IFitsMetadataService metadataService)
    {
        _dataManager = dataManager ?? throw new ArgumentNullException(nameof(dataManager));
        _metadataService = metadataService ?? throw new ArgumentNullException(nameof(metadataService));
        _executablePath = new Lazy<string?>(FindBestExecutable);
    }

    public async Task<AstrometryDiagnosis> DiagnoseIssuesAsync(FitsFileReference fileRef)
    {
        var diagnosis = new AstrometryDiagnosis();
        var header = fileRef.ModifiedHeader ?? await _dataManager.GetHeaderOnlyAsync(fileRef.FilePath);

        if (!_metadataService.GetFocalLength(header).HasValue) diagnosis.MissingItems.Add(AstrometryPrerequisite.FocalLength);
        if (!_metadataService.GetPixelSize(header).HasValue) diagnosis.MissingItems.Add(AstrometryPrerequisite.PixelSize);
        
        return diagnosis;
    }

    public async Task<PlateSolvingResult> SolveFileAsync(
        FitsFileReference fileRef, 
        CancellationToken token = default, 
        IProgress<string>? liveLog = null)
    {
        var result = new PlateSolvingResult();
        string? exePath = _executablePath.Value;

        if (string.IsNullOrEmpty(exePath)) 
        {
            result.Message = "Eseguibile ASTAP non trovato.";
            return result;
        }

        string? tempFilePath = null;
        try
        {
            // 1. Preparazione Ambiente
            var currentHeader = fileRef.ModifiedHeader ?? await _dataManager.GetHeaderOnlyAsync(fileRef.FilePath);
            
            // Creiamo il file temporaneo per ASTAP
            tempFilePath = await PrepareSandboxFile(fileRef);

            // 2. Preparazione Parametri
            string hints = PrepareAstapHints(currentHeader);
            string radius = hints.Contains("-ra") ? "30" : "180"; 
            
            // -z 0: No downsampling, -update: scrivi WCS nel file
            var args = $"-f \"{tempFilePath}\" -r {radius} -update -z 0 {hints}";

            liveLog?.Report($"CONFIG:Raggio {radius}° {(hints.Length > 0 ? "(Hinted)" : "(Blind)")}");

            var logBuilder = new StringBuilder();
            bool solutionFound = false;

            // 3. Esecuzione Processo (ASTAP)
            await RunProcessInternalAsync(exePath, args, line => 
            {
                logBuilder.AppendLine(line);
                
                if (line.Contains("Solution found", StringComparison.OrdinalIgnoreCase) && 
                   !line.Contains("No solution", StringComparison.OrdinalIgnoreCase))
                {
                    solutionFound = true;
                }

                if (IsSignificantLogLine(line))
                {
                    liveLog?.Report($"TOOL:{line.Trim()}");
                }
            }, token);

            // 4. Verifica e Arricchimento (Logica Chirurgica)
            _dataManager.Invalidate(tempFilePath); 
            
            // Leggiamo l'header prodotto da ASTAP
            var astapOutputHeader = await _dataManager.GetHeaderOnlyAsync(tempFilePath);
            
            bool hasWcs = astapOutputHeader != null && 
                          !string.IsNullOrEmpty(_metadataService.GetStringValue(astapOutputHeader, "CRVAL1"));

            if (solutionFound && hasWcs)
            {
                result.Success = true;
                result.Message = "Risoluzione OK";

                // A. CLONAZIONE PULITA
                var finalHeader = _metadataService.CloneHeader(currentHeader!);

                // B. TRASFERIMENTO CHIRURGICO DEL WCS (Incluso TPV/SIP)
                // Lista chiavi standard WCS
                var standardWcsKeys = new[] { 
                    "CTYPE1", "CTYPE2", "CRVAL1", "CRVAL2", "CRPIX1", "CRPIX2", 
                    "CD1_1", "CD1_2", "CD2_1", "CD2_2", 
                    "CUNIT1", "CUNIT2", 
                    "CROTA1", "CROTA2",                 
                    "EQUINOX", "RADESYS", "LONPOLE", "LATPOLE" 
                };

                // Iteriamo tutte le card di output per catturare anche le distorsioni
                foreach (var card in astapOutputHeader!.Cards)
                {
                    string k = card.Key.ToUpperInvariant();
                    
                    // Condizione: È una chiave standard WCS O è un coefficiente di distorsione (PV, A_, B_)
                    bool isWcsKey = standardWcsKeys.Contains(k) ||
                                    k.StartsWith("PV") ||      // Distorsione TPV
                                    k.StartsWith("A_") ||      // Distorsione SIP A
                                    k.StartsWith("B_") ||      // Distorsione SIP B
                                    k.StartsWith("AP_") ||     // Distorsione SIP AP
                                    k.StartsWith("BP_");       // Distorsione SIP BP

                    if (isWcsKey)
                    {
                        finalHeader.RemoveCard(k); // Rimuovi vecchia (se esiste)
                        finalHeader.AddCard(card); // Aggiungi nuova da ASTAP
                    }
                }

                // C. ARRICCHIMENTO METADATI KOMETRA
                _metadataService.SetValue(finalHeader, "PLTSOLVD", true, "Kometra - Plate has been solved");
                _metadataService.SetValue(finalHeader, "SOLVER", "ASTAP", "Kometra - Engine used for solving");
                _metadataService.SetValue(finalHeader, "DATE-SOL", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss"), "Kometra - Solution timestamp (UTC)");

                string historyEntry = FormattableString.Invariant($"Kometra - Plate solved via ASTAP (Radius: {radius} deg).");
                _metadataService.AddValue(finalHeader, "HISTORY", historyEntry, null);

                result.SolvedHeader = finalHeader;
            }
            else
            {
                result.Success = false;
                result.Message = solutionFound ? "Errore lettura WCS output" : "Soluzione non trovata";
            }
            
            result.FullLog = logBuilder.ToString();
        }
        catch (OperationCanceledException)
        {
            result.Message = "Operazione interrotta.";
            result.Success = false;
        }
        catch (Exception ex) 
        { 
            result.Message = $"Errore: {ex.Message}"; 
            result.Success = false;
        }
        finally 
        { 
            if (tempFilePath != null)
            {
                _dataManager.DeleteTemporaryData(tempFilePath); 
            }
        }

        return result;
    }

    private bool IsSignificantLogLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        string l = line.ToLowerInvariant();

        return l.Contains("solution found") || 
               l.Contains("no solution") || 
               l.Contains("solved in") || 
               l.Contains("error") || 
               l.Contains("warning") ||
               l.Contains("failed") ||
               l.Contains("not found") ||
               l.Contains("used stars");
    }
    
    private async Task RunProcessInternalAsync(string exe, string args, Action<string> onLineReceived, CancellationToken token)
    {
        Debug.WriteLine($"[ASTAP-LAUNCH] CMD: {exe}");
        Debug.WriteLine($"[ASTAP-LAUNCH] ARGS: {args}");

        var startInfo = new ProcessStartInfo {
            FileName = exe, 
            Arguments = args, 
            CreateNoWindow = true, 
            UseShellExecute = false,
            RedirectStandardOutput = true, 
            RedirectStandardError = true, 
            StandardOutputEncoding = Encoding.UTF8
        };

        using var process = new Process { StartInfo = startInfo };
        
        try 
        {
            process.Start();
            Debug.WriteLine($"[ASTAP-STATUS] Processo avviato. PID: {process.Id}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ASTAP-ERROR] Impossibile avviare il processo: {ex.Message}");
            throw; 
        }

        using var registration = token.Register(() => 
        {
            try { if (!process.HasExited) process.Kill(true); } 
            catch { /* Processo già chiuso */ }
        });

        var outputDone = new TaskCompletionSource<bool>();
        var errorDone = new TaskCompletionSource<bool>();

        process.OutputDataReceived += (s, e) => {
            if (e.Data == null) outputDone.TrySetResult(true);
            else {
                Debug.WriteLine($"[ASTAP-STDOUT] {e.Data}");
                onLineReceived(e.Data);
            }
        };
        
        process.ErrorDataReceived += (s, e) => {
            if (e.Data == null) errorDone.TrySetResult(true);
            else {
                Debug.WriteLine($"[ASTAP-STDERR] {e.Data}");
                onLineReceived($"[STDERR] {e.Data}");
            }
        };

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await Task.WhenAll(process.WaitForExitAsync(token), outputDone.Task, errorDone.Task);
            Debug.WriteLine($"[ASTAP-EXIT] Codice uscita: {process.ExitCode}");
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine("[ASTAP-CANCEL] Operazione annullata dall'utente.");
            onLineReceived("!!! PROCESSO INTERROTTO DALL'UTENTE !!!");
            throw;
        }
    }

    private async Task<string> PrepareSandboxFile(FitsFileReference fileRef)
    {
        if (fileRef.ModifiedHeader != null)
        {
            var package = await _dataManager.GetDataAsync(fileRef.FilePath);
            
            // [MODIFICA MEF] 
            // Recuperiamo la prima immagine valida (o il primario se non ce ne sono altre)
            // Non possiamo più usare .PixelData direttamente dal package.
            var imageHdu = package.FirstImageHdu ?? package.PrimaryHdu;

            if (imageHdu == null) 
                throw new InvalidOperationException("Impossibile eseguire il plate solving: Il file FITS non contiene immagini.");

            // Salviamo un file temporaneo con l'header modificato dall'utente e i pixel originali
            var temp = await _dataManager.SaveAsTemporaryAsync(
                imageHdu.PixelData, 
                fileRef.ModifiedHeader, // Usa l'header modificato in RAM
                "Astrometry_Mem");
            
            return temp.FilePath;
        }
        
        // Se non ci sono modifiche in RAM, copia fisica del file originale
        return await _dataManager.CreateSandboxCopyAsync(fileRef.FilePath, "Astrometry_Disk");
    }

    private string PrepareAstapHints(FitsHeader? header)
    {
        if (header == null) return "";
        var sb = new StringBuilder();
        var coords = _metadataService.GetTargetCoordinates(header);
        
        if (coords != null)
        {
            sb.AppendFormat(CultureInfo.InvariantCulture, " -ra {0:F4} -spd {1:F4}", coords.RaDeg / 15.0, 90.0 + coords.DecDeg);
        }

        if (_metadataService.GetFocalLength(header) is double f) sb.AppendFormat(CultureInfo.InvariantCulture, " -focal {0:F1}", f);
        if (_metadataService.GetPixelSize(header) is double p) sb.AppendFormat(CultureInfo.InvariantCulture, " -pixsize {0:F2}", p);
    
        return sb.ToString();
    }

    private string? FindBestExecutable()
    {
        var paths = new[] { @"C:\Program Files\astap\astap_cli.exe", @"D:\astap\astap_cli.exe", @"C:\astap\astap.exe" };
        foreach (var p in paths) if (File.Exists(p)) return p;
        return null;
    }
}