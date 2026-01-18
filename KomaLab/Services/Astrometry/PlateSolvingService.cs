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
    private bool _isCliVersion; 

    public PlateSolvingService(
        IFitsDataManager dataManager, 
        IFitsMetadataService metadataService)
    {
        _dataManager = dataManager ?? throw new ArgumentNullException(nameof(dataManager));
        _metadataService = metadataService ?? throw new ArgumentNullException(nameof(metadataService));

        _executablePath = new Lazy<string?>(FindBestExecutable);
    }

    // =======================================================================
    // 1. DIAGNOSI (Uso della Cache)
    // =======================================================================
    
    public async Task<AstrometryDiagnosis> DiagnoseIssuesAsync(string fitsFilePath)
    {
        var diagnosis = new AstrometryDiagnosis();
        
        // Passiamo dal DataManager: se il file è già aperto, lo leggiamo dalla RAM
        var header = await _dataManager.GetHeaderOnlyAsync(fitsFilePath);
        
        if (header == null) return diagnosis; 

        // Controllo coordinate (RA/DEC)
        double ra = _metadataService.GetDoubleValue(header, "RA", double.NaN);
        if (double.IsNaN(ra)) ra = _metadataService.GetDoubleValue(header, "OBJCTRA", double.NaN);
        
        if (double.IsNaN(ra)) 
            diagnosis.MissingItems.Add(AstrometryPrerequisite.ApproximatePosition);

        // Controllo dati ottici e sensore
        if (!_metadataService.GetFocalLength(header).HasValue) 
            diagnosis.MissingItems.Add(AstrometryPrerequisite.FocalLength);

        if (!_metadataService.GetPixelSize(header).HasValue) 
            diagnosis.MissingItems.Add(AstrometryPrerequisite.PixelSize);

        return diagnosis;
    }

    // =======================================================================
    // 2. ESECUZIONE (Logica Sandbox)
    // =======================================================================
    
    public async Task<PlateSolvingResult> SolveFileAsync(
        FitsFileReference fileRef, 
        CancellationToken token = default, 
        IProgress<string>? liveLog = null)
    {
        var result = new PlateSolvingResult();
        string? exePath = _executablePath.Value;

        if (string.IsNullOrEmpty(exePath)) 
        {
            result.Message = "ASTAP non trovato. Verifica l'installazione.";
            result.Success = false;
            return result;
        }

        string? tempFilePath = null;
        try
        {
            // A. Preparazione: Recupero header (probabilmente dalla cache)
            var currentHeader = await _dataManager.GetHeaderOnlyAsync(fileRef.FilePath);
            string hints = PrepareAstapHints(currentHeader);
            string radius = hints.Contains("-ra") ? "30" : "180"; 

            // B. Sandbox: Delega totale al DataManager
            // Crea la copia, genera il path e lo registra per la pulizia futura
            tempFilePath = await _dataManager.CreateSandboxCopyAsync(fileRef.FilePath, "Astrometry");

            // C. Esecuzione ASTAP
            var args = $"-f \"{tempFilePath}\" -r {radius} -update -z 0 {hints}";
            liveLog?.Report($">> Avvio ASTAP (Search Radius: {radius}°)...");

            var logBuilder = new StringBuilder();
            await RunProcessAsync(exePath, args, logBuilder, liveLog, token);

            // D. Verifica Risultato
            // Rileggiamo l'header dal file sandbox modificato da ASTAP
            var solvedHeader = await _dataManager.GetHeaderOnlyAsync(tempFilePath);
            
            if (solvedHeader != null && !string.IsNullOrEmpty(_metadataService.GetStringValue(solvedHeader, "CRVAL1")))
            {
                result.Success = true;
                result.SolvedHeader = solvedHeader;
                result.Message = "Risoluzione completata con successo.";
                
                // Aggiorniamo il riferimento in memoria
                fileRef.ModifiedHeader = solvedHeader;
            }
            else
            {
                result.Message = "Risoluzione fallita (Dati WCS non trovati).";
            }
            
            result.FullLog = logBuilder.ToString();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { 
            result.Message = $"Errore: {ex.Message}"; 
            result.Success = false;
            result.FullLog += $"\nEXCEPTION: {ex.Message}";
        }
        // NOTA: Il blocco 'finally' con la cancellazione manuale del file è opzionale qui,
        // poiché il DataManager pulirà tutto alla fine. Tuttavia, se vogliamo liberare
        // spazio subito:
        finally 
        { 
            if (tempFilePath != null) _dataManager.DeleteTemporaryData(tempFilePath);
        }

        return result;
    }

    // =======================================================================
    // 3. HELPER PRIVATI (Parametri e Processi)
    // =======================================================================

    private string PrepareAstapHints(FitsHeader? header)
    {
        if (header == null) return "";
        var sb = new StringBuilder();
        
        double ra = _metadataService.GetDoubleValue(header, "RA", double.NaN);
        if (double.IsNaN(ra)) ra = _metadataService.GetDoubleValue(header, "OBJCTRA", double.NaN);

        double dec = _metadataService.GetDoubleValue(header, "DEC", double.NaN);
        if (double.IsNaN(dec)) dec = _metadataService.GetDoubleValue(header, "OBJCTDEC", double.NaN);

        if (!double.IsNaN(ra) && !double.IsNaN(dec))
        {
            sb.AppendFormat(CultureInfo.InvariantCulture, "-ra {0:F4} -spd {1:F4} ", ra / 15.0, 90.0 + dec);
        }

        double? focal = _metadataService.GetFocalLength(header);
        if (focal.HasValue) 
            sb.AppendFormat(CultureInfo.InvariantCulture, "-focal {0:F1} ", focal.Value);
        
        return sb.ToString();
    }

    private string? FindBestExecutable()
    {
        var paths = new[] {
            @"C:\Program Files\astap\astap_cli.exe", 
            @"C:\astap\astap_cli.exe",
            @"C:\Program Files\astap\astap.exe", 
            @"C:\astap\astap.exe"
        };

        foreach (var path in paths)
        {
            if (File.Exists(path))
            {
                _isCliVersion = path.EndsWith("cli.exe", StringComparison.OrdinalIgnoreCase);
                return path;
            }
        }
        return null;
    }

    private async Task RunProcessAsync(string exe, string args, StringBuilder fullLog, IProgress<string>? live, CancellationToken token)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = exe, Arguments = args, 
            CreateNoWindow = true,
            UseShellExecute = !_isCliVersion,
            RedirectStandardOutput = _isCliVersion, 
            RedirectStandardError = _isCliVersion
        };

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        
        if (_isCliVersion)
        {
            process.OutputDataReceived += (s, e) => {
                if (e.Data == null) return;
                fullLog.AppendLine(e.Data);
                var clean = CleanAstapLine(e.Data); 
                if (clean != null) live?.Report(clean);
            };
        }

        var tcs = new TaskCompletionSource();
        process.Exited += (s, e) => tcs.TrySetResult();
        
        process.Start();
        if (_isCliVersion) process.BeginOutputReadLine();

        using var reg = token.Register(() => { 
            try { process.Kill(); } catch { } 
            tcs.TrySetCanceled(); 
        });

        await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(90), token));
        
        if (!process.HasExited) 
        { 
            try { process.Kill(); } catch { } 
        }
    }

    private string? CleanAstapLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        if (line.Contains("x:=") || line.Contains("quads selected")) return null;
        
        if (line.StartsWith("Trying FOV:", StringComparison.OrdinalIgnoreCase))
            return $"   > Campo: {line.Replace("Trying FOV:", "").Trim()}°";
            
        return line.Trim();
    }
}