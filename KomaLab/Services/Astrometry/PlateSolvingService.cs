using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Globalization;
using KomaLab.Models.Astrometry;
using KomaLab.Models.Fits;
using KomaLab.Services.Fits;
using KomaLab.Services.Fits.Engine;

namespace KomaLab.Services.Astrometry;

/// <summary>
/// Wrapper per il motore di risoluzione astrometrica ASTAP.
/// Gestisce la risoluzione non-distruttiva tramite sandbox e ottimizzazione dei parametri.
/// </summary>
public class PlateSolvingService : IPlateSolvingService
{
    private readonly IFitsIoService _ioService;
    private readonly IFitsMetadataService _metadataService;
    private readonly FitsReader _reader;
    private string? _selectedExePath;
    private bool _isCliVersion;
    private bool _hasSearched;

    public PlateSolvingService(
        IFitsIoService ioService, 
        IFitsMetadataService metadataService, 
        FitsReader reader)
    {
        _ioService = ioService ?? throw new ArgumentNullException(nameof(ioService));
        _metadataService = metadataService ?? throw new ArgumentNullException(nameof(metadataService));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    private void FindBestExecutable()
    {
        if (_hasSearched) return;

        var cliPaths = new List<string>
        {
            @"C:\Program Files\astap\astap_cli.exe",
            @"C:\Program Files (x86)\astap\astap_cli.exe",
            @"C:\astap\astap_cli.exe",
            @"D:\astap\astap_cli.exe",
        };

        foreach (var path in cliPaths)
        {
            if (File.Exists(path)) { _selectedExePath = path; _isCliVersion = true; _hasSearched = true; return; }
        }

        var guiPaths = new List<string>
        {
            @"C:\Program Files\astap\astap.exe",
            @"C:\Program Files (x86)\astap\astap.exe",
            @"C:\astap\astap.exe",
            @"D:\astap\astap.exe",
        };

        foreach (var path in guiPaths)
        {
            if (File.Exists(path)) { _selectedExePath = path; _isCliVersion = false; _hasSearched = true; return; }
        }
        
        _selectedExePath = null;
        _hasSearched = true;
    }

    public async Task<PlateSolvingResult> SolveAsync(
        string fitsFilePath, 
        CancellationToken token = default, 
        Action<string>? onLogReceived = null)
    {
        var result = new PlateSolvingResult();
        FindBestExecutable();

        if (string.IsNullOrEmpty(_selectedExePath)) 
        {
            result.Message = "ASTAP non trovato. Installa ASTAP e il catalogo stellare.";
            result.Success = false;
            return result;
        }

        string? tempFilePath = null;
        try
        {
            // 1. Legge l'header originale per i suggerimenti (Hints)
            // MODIFICA: Metodo rinominato nella nuova interfaccia IFitsIoService
            var currentHeader = await _ioService.ReadHeaderAsync(fitsFilePath);
            string hints = PrepareAstapHints(currentHeader);

            // Logica Blind vs Hinted: Se mancano RA/DEC, cerchiamo su tutto il cielo (180°)
            string radius = hints.Contains("-ra") ? "30" : "180";

            // 2. Crea Sandbox: Copia il file in TEMP per non toccare l'originale
            tempFilePath = Path.Combine(Path.GetTempPath(), $"solve_{Guid.NewGuid()}.fits");
            await Task.Run(() => File.Copy(fitsFilePath, tempFilePath, true), token);

            // 3. Esecuzione ASTAP
            // -update: Scrive il WCS nell'header del file (temp)
            // -z 0: Downsample auto (0=auto)
            var args = $"-f \"{tempFilePath}\" -r {radius} -update -z 0 {hints}";
            onLogReceived?.Invoke($">> Avvio ASTAP (Search Radius: {radius}°)...");

            var logBuilder = new StringBuilder();
            var startInfo = new ProcessStartInfo
            {
                FileName = _selectedExePath,
                Arguments = args,
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
                    logBuilder.AppendLine(e.Data);
                    var clean = CleanAstapLine(e.Data);
                    if (clean != null) onLogReceived?.Invoke(clean);
                };
            }

            var tcs = new TaskCompletionSource<bool>();
            process.Exited += (s, e) => tcs.TrySetResult(true);
            process.Start();
            if (_isCliVersion) { process.BeginOutputReadLine(); }

            // Timeout di sicurezza 90 secondi
            var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(90), token));

            if (completedTask == tcs.Task && !token.IsCancellationRequested)
            {
                // 4. Lettura Risultato
                // ASTAP ha modificato il file in tempFilePath.
                // Usiamo il reader diretto per estrarre l'header risolto.
                using var stream = File.OpenRead(tempFilePath);
                var solvedHeader = _reader.ReadHeader(stream);
                
                // CRVAL1 è la chiave standard WCS per l'Ascensione Retta. Se c'è, è risolto.
                if (solvedHeader.ContainsKey("CRVAL1"))
                {
                    result.Success = true;
                    result.SolvedHeader = solvedHeader; 
                }
                else
                {
                    result.Message = "Risoluzione fallita (Nessun WCS trovato).";
                }
            }
            else
            {
                try { process.Kill(); } catch { }
                result.Message = "Timeout risoluzione o annullato.";
            }
            result.FullLog = logBuilder.ToString();
        }
        catch (Exception ex) 
        { 
            result.Message = $"Errore Critico: {ex.Message}"; 
            result.Success = false;
        }
        finally 
        { 
            if (tempFilePath != null && File.Exists(tempFilePath)) 
            {
                try { File.Delete(tempFilePath); } catch { }
            } 
        }

        return result;
    }

    // --- LOGICA HINTS (Preparazione parametri) ---

    private string PrepareAstapHints(FitsHeader? header)
    {
        if (header == null) return "";
        var sb = new StringBuilder();
        
        // Tentativo di estrazione coordinate (Standard o MaxImDL/SGP style)
        double? ra = header.GetValue<double>("RA") ?? header.GetValue<double>("OBJCTRA");
        double? dec = header.GetValue<double>("DEC") ?? header.GetValue<double>("OBJCTDEC");

        if (ra.HasValue && dec.HasValue)
        {
            sb.AppendFormat(CultureInfo.InvariantCulture, "-ra {0:F4} -spd {1:F4} ", ra.Value / 15.0, 90.0 + dec.Value);
        }

        double? focal = _metadataService.GetFocalLength(header);
        double? pixSize = _metadataService.GetPixelSize(header);

        if (focal.HasValue && focal.Value > 0) sb.AppendFormat(CultureInfo.InvariantCulture, "-focal {0:F1} ", focal.Value);
        if (pixSize.HasValue && pixSize.Value > 0) sb.AppendFormat(CultureInfo.InvariantCulture, "-pixsize {0:F2} ", pixSize.Value);

        return sb.ToString();
    }

    public async Task<string> DiagnoseIssuesAsync(string fitsFilePath)
    {
        // MODIFICA: Rinominato ReadHeaderOnlyAsync -> ReadHeaderAsync
        var header = await _ioService.ReadHeaderAsync(fitsFilePath);
        
        if (header == null) return "Impossibile leggere l'header del file.";

        var missing = new List<string>();

        if (!header.ContainsKey("RA") && !header.ContainsKey("OBJCTRA")) 
            missing.Add("Coordinate RA/DEC (Posizione approssimativa)");

        if (!_metadataService.GetFocalLength(header).HasValue) 
            missing.Add("Lunghezza Focale (Focal Length)");

        if (!_metadataService.GetPixelSize(header).HasValue) 
            missing.Add("Dimensione Pixel (Pixel Size)");

        if (missing.Count == 0) return "Metadati completi (RA, DEC, Scala).";

        var sb = new StringBuilder();
        sb.AppendLine("Metadati incompleti. Mancano:");
        foreach (var item in missing) sb.AppendLine($"   - {item}");

        return sb.ToString().TrimEnd();
    }

    private string? CleanAstapLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;

        // Filtri rumore log ASTAP (Invariati)
        if (System.Text.RegularExpressions.Regex.IsMatch(line, @"^\d+d,")) return null;
        if (line.Contains("x:=") || line.Contains("y:=") || line.Contains("Solution[\"]")) return null;

        string[] noiseKeywords = {
            "ASTAP solver version", "Quad tolerance", "Minimum star size", "Speed:", 
            "Using star database", "Database limit", "Creating grayscale", "Search radius:",
            "Start position:", "Image height:", "Binning:", "Image dimensions:",
            "quads selected matching", "Used stars down to", "of", "required for the",
            "Solution found" 
        };

        if (noiseKeywords.Any(k => line.Contains(k, StringComparison.OrdinalIgnoreCase))) return null;

        if (line.StartsWith("Trying FOV:", StringComparison.OrdinalIgnoreCase))
            return $"   > Tentativo campo visivo: {line.Replace("Trying FOV:", "").Trim()}°";

        if (line.Contains("Exception") || line.Contains("Error") || line.Contains("division by zero"))
            return $"   [MOTORE ASTAP] {line.Trim()}";

        return line.Trim();
    }
}