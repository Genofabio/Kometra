using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Kometra.Infrastructure; // Localizzazione
using Kometra.Models.Astrometry;
using Kometra.Models.Astrometry.Solving;
using Kometra.Models.Fits;
using Kometra.Models.Fits.Structure;
using Kometra.Services.Fits;
using Kometra.Services.Fits.Metadata;
using Kometra.Services; // Namespace per IConfigurationService

namespace Kometra.Services.Astrometry;

public class PlateSolvingService : IPlateSolvingService
{
    private readonly IFitsDataManager _dataManager; 
    private readonly IFitsMetadataService _metadataService;
    private readonly IConfigurationService _configService;

    public PlateSolvingService(
        IFitsDataManager dataManager, 
        IFitsMetadataService metadataService,
        IConfigurationService configService)
    {
        _dataManager = dataManager ?? throw new ArgumentNullException(nameof(dataManager));
        _metadataService = metadataService ?? throw new ArgumentNullException(nameof(metadataService));
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
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
        
        // 1. Recupero l'eseguibile esclusivamente dalle impostazioni
        string? exePath = GetConfiguredExecutablePath();

        if (string.IsNullOrEmpty(exePath)) 
        {
            // Restituisce l'errore se il path non è impostato o il file non esiste
            result.Message = LocalizationManager.Instance["PlateErrorExeNotFound"];
            result.Success = false;
            return result;
        }

        string? tempFilePath = null;
        try
        {
            // 2. Preparazione Ambiente
            var currentHeader = fileRef.ModifiedHeader ?? await _dataManager.GetHeaderOnlyAsync(fileRef.FilePath);
            
            // Creiamo il file temporaneo per ASTAP
            tempFilePath = await PrepareSandboxFile(fileRef);

            // 3. Preparazione Parametri
            string hints = PrepareAstapHints(currentHeader);
            string radius = hints.Contains("-ra") ? "30" : "180"; 
            
            // -z 0: No downsampling, -update: scrivi WCS nel file
            var args = $"-f \"{tempFilePath}\" -r {radius} -update -z 0 {hints}";

            string hintLabel = hints.Length > 0 
                ? LocalizationManager.Instance["PlateHinted"] 
                : LocalizationManager.Instance["PlateBlind"];

            liveLog?.Report($"CONFIG:{string.Format(LocalizationManager.Instance["PlateConfigRadius"], radius, hintLabel)}");

            var logBuilder = new StringBuilder();
            bool solutionFound = false;

            // 4. Esecuzione Processo (ASTAP)
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

            // 5. Verifica e Arricchimento (Logica Chirurgica)
            _dataManager.Invalidate(tempFilePath); 
            
            // Leggiamo l'header prodotto da ASTAP
            var astapOutputHeader = await _dataManager.GetHeaderOnlyAsync(tempFilePath);
            
            bool hasWcs = astapOutputHeader != null && 
                          !string.IsNullOrEmpty(_metadataService.GetStringValue(astapOutputHeader, "CRVAL1"));

            if (solutionFound && hasWcs)
            {
                result.Success = true;
                result.Message = LocalizationManager.Instance["PlateStatusSuccess"];

                // A. CLONAZIONE PULITA
                var finalHeader = _metadataService.CloneHeader(currentHeader!);

                // B. TRASFERIMENTO CHIRURGICO DEL WCS (Incluso TPV/SIP)
                var standardWcsKeys = new[] { 
                    "CTYPE1", "CTYPE2", "CRVAL1", "CRVAL2", "CRPIX1", "CRPIX2", 
                    "CD1_1", "CD1_2", "CD2_1", "CD2_2", 
                    "CUNIT1", "CUNIT2", 
                    "CROTA1", "CROTA2",                 
                    "EQUINOX", "RADESYS", "LONPOLE", "LATPOLE" 
                };

                foreach (var card in astapOutputHeader!.Cards)
                {
                    string k = card.Key.ToUpperInvariant();
                    
                    bool isWcsKey = standardWcsKeys.Contains(k) ||
                                    k.StartsWith("PV") ||      // Distorsione TPV
                                    k.StartsWith("A_") ||      // Distorsione SIP A
                                    k.StartsWith("B_") ||      // Distorsione SIP B
                                    k.StartsWith("AP_") ||     // Distorsione SIP AP
                                    k.StartsWith("BP_");       // Distorsione SIP BP

                    if (isWcsKey)
                    {
                        finalHeader.RemoveCard(k); 
                        finalHeader.AddCard(card); 
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
                result.Message = solutionFound 
                    ? LocalizationManager.Instance["PlateErrorWcsRead"] 
                    : LocalizationManager.Instance["PlateErrorNoSolution"];
            }
            
            result.FullLog = logBuilder.ToString();
        }
        catch (OperationCanceledException)
        {
            result.Message = LocalizationManager.Instance["StatusCancelled"];
            result.Success = false;
        }
        catch (Exception ex) 
        { 
            result.Message = string.Format(LocalizationManager.Instance["ErrorGeneric"], ex.Message); 
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

    private string? GetConfiguredExecutablePath()
    {
        string folder = _configService.Current.AstapFolder;
        
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return null;

        // Gestione estensione per Windows
        string[] exeNames = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new[] { "astap_cli.exe", "astap.exe" }
            : new[] { "astap_cli", "astap" };

        foreach (var name in exeNames)
        {
            string fullPath = Path.Combine(folder, name);
            if (File.Exists(fullPath)) return fullPath;
        }

        return null;
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
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ASTAP-ERROR] Impossibile avviare il processo: {ex.Message}");
            throw; 
        }

        using var registration = token.Register(() => 
        {
            try { if (!process.HasExited) process.Kill(true); } 
            catch { }
        });

        var outputDone = new TaskCompletionSource<bool>();
        var errorDone = new TaskCompletionSource<bool>();

        process.OutputDataReceived += (s, e) => {
            if (e.Data == null) outputDone.TrySetResult(true);
            else onLineReceived(e.Data);
        };
        
        process.ErrorDataReceived += (s, e) => {
            if (e.Data == null) errorDone.TrySetResult(true);
            else onLineReceived($"[STDERR] {e.Data}");
        };

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await Task.WhenAll(process.WaitForExitAsync(token), outputDone.Task, errorDone.Task);
        }
        catch (OperationCanceledException)
        {
            onLineReceived(LocalizationManager.Instance["PlateProcessInterrupted"]);
            throw;
        }
    }

    private async Task<string> PrepareSandboxFile(FitsFileReference fileRef)
    {
        if (fileRef.ModifiedHeader != null)
        {
            var package = await _dataManager.GetDataAsync(fileRef.FilePath);
            var imageHdu = package.FirstImageHdu ?? package.PrimaryHdu;

            if (imageHdu == null) 
                throw new InvalidOperationException(LocalizationManager.Instance["PlateErrorNoImages"]);

            var temp = await _dataManager.SaveAsTemporaryAsync(
                imageHdu.PixelData, 
                fileRef.ModifiedHeader, 
                "Astrometry_Mem");
            
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
}