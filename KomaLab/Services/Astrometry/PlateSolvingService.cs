using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
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
        var coords = _metadataService.GetTargetCoordinates(header);
        
        if (coords == null) diagnosis.MissingItems.Add(AstrometryPrerequisite.ApproximatePosition);
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
            var header = fileRef.ModifiedHeader ?? await _dataManager.GetHeaderOnlyAsync(fileRef.FilePath);
            tempFilePath = await PrepareSandboxFile(fileRef);

            // 2. Preparazione Parametri
            string hints = PrepareAstapHints(header);
            string radius = hints.Contains("-ra") ? "30" : "180"; 
            var args = $"-f \"{tempFilePath}\" -r {radius} -update -z 0 {hints}";

            liveLog?.Report($"CONFIG:Raggio {radius}° {(hints.Length > 0 ? "(Hinted)" : "(Blind)")}");

            var logBuilder = new StringBuilder();
            bool solutionFound = false;

            // 3. Esecuzione Processo
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

            // 4. Verifica e Arricchimento
            _dataManager.Invalidate(tempFilePath);
            var solvedHeader = await _dataManager.GetHeaderOnlyAsync(tempFilePath);
            bool hasWcs = solvedHeader != null && !string.IsNullOrEmpty(_metadataService.GetStringValue(solvedHeader, "CRVAL1"));

            if (solutionFound && hasWcs)
            {
                result.Success = true;
                result.SolvedHeader = solvedHeader;
                result.Message = "Risoluzione OK";

                // --- Arricchimento Metadati KomaLab ---
                _metadataService.SetValue(solvedHeader!, "CREATOR", "KomaLab", "Software that coordinated the solve");
                _metadataService.SetValue(solvedHeader!, "PLTSOLVD", true, "KomaLab - Plate has been solved");
                _metadataService.SetValue(solvedHeader!, "SOLVER", "ASTAP", "KomaLab - Engine used for solving");
                _metadataService.SetValue(solvedHeader!, "DATE-SOL", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss"), "KomaLab - Date of astrometric solution");

                string historyEntry = $"KomaLab - Plate solved via ASTAP (Radius: {radius} deg).";
                _metadataService.AddValue(solvedHeader!, "HISTORY", historyEntry, null);
            }
            else
            {
                result.Success = false;
                result.Message = solutionFound ? "Errore scrittura WCS" : "Soluzione non trovata";
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
        
        using var registration = token.Register(() => 
        {
            try { if (!process.HasExited) process.Kill(true); } 
            catch { /* Processo già chiuso */ }
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

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await Task.WhenAll(process.WaitForExitAsync(token), outputDone.Task, errorDone.Task);
        }
        catch (OperationCanceledException)
        {
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