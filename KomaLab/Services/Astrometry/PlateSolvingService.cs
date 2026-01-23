using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KomaLab.Models.Astrometry;
using KomaLab.Models.Astrometry.Solving;
using KomaLab.Models.Fits;
using KomaLab.Models.Fits.Structure;
using KomaLab.Services.Fits;
using KomaLab.Services.Fits.Metadata;

namespace KomaLab.Services.Astrometry;

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
            // Recuperiamo l'header corrente (quello in memoria se modificato, o da disco)
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
            _dataManager.Invalidate(tempFilePath); // Puliamo la cache per leggere il file modificato da ASTAP
            
            // Leggiamo l'header prodotto da ASTAP (che contiene il WCS ma potrebbe essere "sporco")
            var astapOutputHeader = await _dataManager.GetHeaderOnlyAsync(tempFilePath);
            
            bool hasWcs = astapOutputHeader != null && 
                          !string.IsNullOrEmpty(_metadataService.GetStringValue(astapOutputHeader, "CRVAL1"));

            if (solutionFound && hasWcs)
            {
                result.Success = true;
                result.Message = "Risoluzione OK";

                // A. CLONAZIONE PULITA
                // Partiamo dall'header originale del file (preservando TELESCOP, OBJECT, HISTORY vecchie, ecc.)
                // e garantendo che la struttura (incluso END) sia corretta.
                var finalHeader = _metadataService.CloneHeader(currentHeader!);

                // B. TRASFERIMENTO CHIRURGICO DEL WCS
                // Copiamo solo le chiavi astrometriche dall'output di ASTAP al nostro header pulito.
                var wcsKeys = new[] { 
                    "CTYPE1", "CTYPE2", "CRVAL1", "CRVAL2", "CRPIX1", "CRPIX2", 
                    "CD1_1", "CD1_2", "CD2_1", "CD2_2", // Matrice standard
                    "CUNIT1", "CUNIT2", 
                    "CROTA1", "CROTA2",                 // Rotazione legacy
                    "EQUINOX", "RADESYS", "LONPOLE", "LATPOLE" 
                };

                foreach (var key in wcsKeys)
                {
                    // Cerchiamo la card nel risultato di ASTAP
                    var newCard = astapOutputHeader!.Cards.FirstOrDefault(c => c.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
                    
                    if (newCard != null)
                    {
                        // Rimuoviamo la vecchia chiave WCS se esisteva
                        finalHeader.RemoveCard(key);
                        // Aggiungiamo la nuova calcolata da ASTAP
                        finalHeader.AddCard(newCard); 
                    }
                }

                // C. ARRICCHIMENTO METADATI KOMALAB
                // Usiamo il MetadataService per impostare i valori in modo sicuro.
                
                _metadataService.SetValue(finalHeader, "PLTSOLVD", true, "KomaLab - Plate has been solved");
                _metadataService.SetValue(finalHeader, "SOLVER", "ASTAP", "KomaLab - Engine used for solving");
                
                // DATE-SOL: Data della risoluzione matematica (diverso da DATE creazione file)
                _metadataService.SetValue(finalHeader, "DATE-SOL", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss"), "KomaLab - Solution timestamp (UTC)");

                // HISTORY: Nota per l'utente
                string historyEntry = FormattableString.Invariant($"KomaLab - Plate solved via ASTAP (Radius: {radius} deg).");
                _metadataService.AddValue(finalHeader, "HISTORY", historyEntry, null);

                // Assegniamo l'header pulito e aggiornato al risultato
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
        // LOG 1: Vediamo esattamente cosa stiamo lanciando
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
        
        // LOG 2: Verifica se il processo parte davvero
        try 
        {
            process.Start();
            Debug.WriteLine($"[ASTAP-STATUS] Processo avviato. PID: {process.Id}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ASTAP-ERROR] Impossibile avviare il processo: {ex.Message}");
            throw; // Rilancia per gestire l'errore a monte
        }

        using var registration = token.Register(() => 
        {
            try { if (!process.HasExited) process.Kill(true); } 
            catch { /* Processo già chiuso */ }
        });

        var outputDone = new TaskCompletionSource<bool>();
        var errorDone = new TaskCompletionSource<bool>();

        process.OutputDataReceived += (s, e) => {
            if (e.Data == null) 
            {
                outputDone.TrySetResult(true);
            }
            else 
            {
                // LOG 3: Leggiamo cosa dice ASTAP in tempo reale
                Debug.WriteLine($"[ASTAP-STDOUT] {e.Data}");
                onLineReceived(e.Data);
            }
        };
        
        process.ErrorDataReceived += (s, e) => {
            if (e.Data == null) 
            {
                errorDone.TrySetResult(true);
            }
            else 
            {
                // LOG 4: Leggiamo eventuali errori di ASTAP
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
            var temp = await _dataManager.SaveAsTemporaryAsync(package.PixelData, fileRef.ModifiedHeader, "Astrometry_Mem");
            return temp.FilePath;
        }
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