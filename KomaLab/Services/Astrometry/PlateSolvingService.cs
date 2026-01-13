using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KomaLab.Models.Astrometry;

namespace KomaLab.Services.Astrometry;

// ---------------------------------------------------------------------------
// FILE: PlateSolvingService.cs
// RUOLO: Wrapper Processo Esterno & Logica Astrometrica
// DESCRIZIONE:
// Gestisce l'interazione con il motore di risoluzione astrometrica ASTAP e 
// fornisce strumenti di diagnostica sui file FITS per identificare cause di fallimento.
//
// RESPONSABILITÀ:
// 1. Discovery: Trova l'eseguibile ASTAP (CLI o GUI) nel sistema.
// 2. Esecuzione: Lancia il processo con parametri ottimizzati per il blind solve.
// 3. Monitoraggio: Gestisce timeout, cancellazione e ridirezione dei log.
// 4. Diagnostica: Analizza l'header FITS per verificare la presenza di metadati 
//    critici (RA, DEC, Focale) necessari al solver.
// ---------------------------------------------------------------------------

public class PlateSolvingService : IPlateSolvingService
{
    private string? _cachedExePath;
    private bool _isCliVersion;
    private bool _hasSearched;

    public async Task<PlateSolvingResult> SolveAsync(
        string fitsFilePath, 
        CancellationToken token = default, 
        Action<string>? onLogReceived = null)
    {
        var result = new PlateSolvingResult();
        
        // 1. Discovery Eseguibile
        EnsureExecutableFound();

        if (string.IsNullOrEmpty(_cachedExePath)) 
        {
            result.Message = "ERRORE: Eseguibile ASTAP non trovato. Assicurati che ASTAP sia installato.";
            result.Success = false;
            return result;
        }

        // 2. Configurazione Parametri ASTAP
        // -f: file input
        // -r 180: blind solve (cerca in tutto il cielo, range 180 gradi)
        // -update: scrive il WCS (header) direttamente nel file FITS originale
        // -z 0: downsample factor (0 = auto)
        // -fov 0: campo visivo (0 = auto/da header)
        var args = $"-f \"{fitsFilePath}\" -r 180 -update -z 0 -fov 0";
        
        var startInfo = new ProcessStartInfo
        {
            FileName = _cachedExePath,
            Arguments = args,
            CreateNoWindow = true,
            UseShellExecute = !_isCliVersion, 
            WindowStyle = ProcessWindowStyle.Hidden
        };

        if (_isCliVersion)
        {
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
        }

        using var process = new Process();
        process.StartInfo = startInfo;
        process.EnableRaisingEvents = true;
        
        var logBuilder = new StringBuilder();

        SetupLogRedirection(process, logBuilder, onLogReceived);

        var tcs = new TaskCompletionSource<bool>();
        using var registration = token.Register(() => tcs.TrySetCanceled());

        process.Exited += (_, _) => tcs.TrySetResult(true);

        try
        {
            process.Start();

            if (_isCliVersion)
            {
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }

            // ASTAP può impiegare tempo per blind solve, diamo 90 secondi.
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(90), token);
            var processTask = tcs.Task;

            var completedTask = await Task.WhenAny(processTask, timeoutTask);

            if (completedTask == timeoutTask || token.IsCancellationRequested)
            {
                KillProcessSafe(process);
                result.Success = false;
                result.Message = token.IsCancellationRequested ? "Operazione annullata dall'utente." : "Timeout operazione (90s).";
            }
            else
            {
                if (_isCliVersion) await Task.Delay(100); 

                result.FullLog = logBuilder.ToString();
                result.Success = EvaluateSuccess(process, result.FullLog);
                result.Message = result.Success ? "Plate Solving completato con successo (WCS scritto)." : "Soluzione non trovata.";
            }
        }
        catch (Exception ex)
        {
            result.Message = $"Eccezione critica durante esecuzione ASTAP: {ex.Message}";
            result.Success = false;
        }

        return result;
    }

    /// <summary>
    /// Analizza il file FITS per trovare mancanze nei metadati che impediscono la risoluzione.
    /// (Logica spostata dal ViewModel per rispettare MVVM e SoC).
    /// </summary>
    public async Task<string> DiagnoseIssuesAsync(string fitsFilePath)
    {
        return await Task.Run(() =>
        {
            var issues = new List<string>();
            nom.tam.fits.Fits? f = null;
            try
            {
                f = new nom.tam.fits.Fits(fitsFilePath);
                var hdu = f.ReadHDU();
                if (hdu == null) return ">> DIAGNOSI: File FITS corrotto o non leggibile.";

                var header = hdu.Header;
                
                // Verifica Coordinate
                bool hasRa = header.ContainsKey("RA") || header.ContainsKey("OBJCTRA");
                bool hasDec = header.ContainsKey("DEC") || header.ContainsKey("OBJCTDEC");
                
                // Verifica Scala/Ottica
                bool hasFocal = header.ContainsKey("FOCALLEN") || header.ContainsKey("FOCAL");
                bool hasPixel = header.ContainsKey("XPIXSZ") || header.ContainsKey("PIXSIZE");
                bool hasScale = header.ContainsKey("PLTSCALE"); 

                if (!hasRa || !hasDec) 
                    issues.Add("Coordinate approssimative (RA/DEC) mancanti nell'header.");
                
                if (!hasScale && (!hasFocal || !hasPixel)) 
                    issues.Add("Dati ottici insufficienti: mancano Focale o Dimensione Pixel (necessari per calcolare la scala).");
            }
            catch (Exception ex)
            { 
                return $">> DIAGNOSI FALLITA: {ex.Message}"; 
            }
            finally 
            { 
                f?.Close(); 
            }

            if (issues.Count > 0)
            {
                var sb = new StringBuilder();
                sb.AppendLine(">> DIAGNOSI PROBABILE:");
                foreach (var issue in issues) sb.AppendLine($"   - {issue}");
                return sb.ToString().TrimEnd();
            }
            
            return ">> DIAGNOSI: Metadati header corretti. Il fallimento potrebbe dipendere da scarsa qualità dell'immagine, troppe poche stelle o catalogo ASTAP non installato.";
        });
    }

    private void EnsureExecutableFound()
    {
        if (_hasSearched) return;

        var searchPaths = new List<string>();
        
        void AddVariations(string basePath)
        {
            if (string.IsNullOrEmpty(basePath)) return;
            searchPaths.Add(Path.Combine(basePath, "astap", "astap_cli.exe"));
            searchPaths.Add(Path.Combine(basePath, "astap", "astap.exe"));
        }

        AddVariations(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        AddVariations(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        
        string[] drives = { "C:\\", "D:\\", "E:\\" };
        foreach (var drive in drives)
        {
            if (Directory.Exists(drive))
            {
                searchPaths.Add(Path.Combine(drive, "astap", "astap_cli.exe"));
                searchPaths.Add(Path.Combine(drive, "astap", "astap.exe"));
            }
        }

        foreach (var path in searchPaths)
        {
            if (File.Exists(path))
            {
                _cachedExePath = path;
                _isCliVersion = path.EndsWith("astap_cli.exe", StringComparison.OrdinalIgnoreCase);
                break;
            }
        }

        _hasSearched = true;
    }

    private void SetupLogRedirection(Process process, StringBuilder buffer, Action<string>? uiCallback)
    {
        if (!_isCliVersion) return;

        process.OutputDataReceived += (_, e) => 
        { 
            if (!string.IsNullOrEmpty(e.Data)) 
            {
                buffer.AppendLine(e.Data); 
                uiCallback?.Invoke(e.Data);
            } 
        };
        
        process.ErrorDataReceived += (_, e) => 
        { 
            if (!string.IsNullOrEmpty(e.Data)) 
            {
                string err = $"[STDERR] {e.Data}";
                buffer.AppendLine(err);
                uiCallback?.Invoke(err);
            } 
        };
    }

    private bool EvaluateSuccess(Process process, string output)
    {
        if (_isCliVersion)
        {
            return output.Contains("Solution found", StringComparison.InvariantCultureIgnoreCase) 
                || output.Contains("PLTSOLVD=T", StringComparison.InvariantCultureIgnoreCase)
                || output.Contains("created wcs", StringComparison.InvariantCultureIgnoreCase)
                || output.Contains("Updating header", StringComparison.InvariantCultureIgnoreCase);
        }
        return process.ExitCode == 0;
    }

    private void KillProcessSafe(Process p)
    {
        try { if (!p.HasExited) p.Kill(); } catch { }
    }
}