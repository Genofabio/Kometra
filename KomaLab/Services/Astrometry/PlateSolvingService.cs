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
            // 1. Recupero Header
            var header = fileRef.ModifiedHeader ?? await _dataManager.GetHeaderOnlyAsync(fileRef.FilePath);
            
            // 2. Creazione Sandbox Fisica
            tempFilePath = await PrepareSandboxFile(fileRef);

            // 3. Preparazione Parametri
            string hints = PrepareAstapHints(header);
            string radius = hints.Contains("-ra") ? "30" : "180"; 

            var args = $"-f \"{tempFilePath}\" -r {radius} -update -z 0 {hints}";

            // LOG SEMANTICO: Inviamo il dato, il VM aggiungerà il "> Config:"
            liveLog?.Report($"CONFIG:Raggio {radius}° {(hints.Length > 0 ? "(Hinted)" : "(Blind)")}");

            var logBuilder = new StringBuilder();
            bool solutionFound = false;

            // 4. Esecuzione Processo
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
                    // LOG SEMANTICO: Inviamo la riga, il VM aggiungerà il pipe "|"
                    liveLog?.Report($"TOOL:{line.Trim()}");
                }
            }, token);

            // 5. Sincronizzazione Dati
            _dataManager.Invalidate(tempFilePath);
            var solvedHeader = await _dataManager.GetHeaderOnlyAsync(tempFilePath);
            
            bool hasWcs = solvedHeader != null && !string.IsNullOrEmpty(_metadataService.GetStringValue(solvedHeader, "CRVAL1"));

            // 6. Costruzione Risultato (Senza stringhe UI)
            if (solutionFound && hasWcs)
            {
                result.Success = true;
                result.SolvedHeader = solvedHeader; // Passiamo l'oggetto completo
                result.Message = "Risoluzione OK";
            }
            else
            {
                result.Success = false;
                result.Message = solutionFound ? "Errore scrittura WCS" : "Soluzione non trovata";
            }
            
            result.FullLog = logBuilder.ToString();
        }
        catch (Exception ex) 
        { 
            result.Message = $"Errore: {ex.Message}"; 
            result.Success = false;
        }
        finally 
        { 
            if (tempFilePath != null) _dataManager.DeleteTemporaryData(tempFilePath); 
        }

        return result;
    }

    private bool IsSignificantLogLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        string l = line.ToLowerInvariant();

        return l.Contains("solution found") || 
               l.Contains("no solution found") || 
               l.Contains("solved in") || 
               l.Contains("error") || 
               l.Contains("warning") || 
               l.Contains("used stars");
    }

    private async Task RunProcessInternalAsync(string exe, string args, Action<string> onLineReceived, CancellationToken token)
    {
        var startInfo = new ProcessStartInfo {
            FileName = exe, Arguments = args, CreateNoWindow = true, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8
        };

        using var process = new Process { StartInfo = startInfo };
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
        await Task.WhenAll(process.WaitForExitAsync(token), outputDone.Task, errorDone.Task);
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