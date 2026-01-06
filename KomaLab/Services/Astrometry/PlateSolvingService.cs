using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KomaLab.Services.Astrometry;

public class PlateSolvingResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string FullLog { get; set; } = string.Empty;
}

public class PlateSolvingService
{
    private string? _selectedExePath;
    private bool _isCliVersion;

    private void FindBestExecutable()
    {
        var cliPaths = new List<string>
        {
            @"C:\Program Files\astap\astap_cli.exe",
            @"C:\Program Files (x86)\astap\astap_cli.exe",
            @"C:\astap\astap_cli.exe",
            @"D:\Program Files\astap\astap_cli.exe",
            @"D:\Program Files (x86)\astap\astap_cli.exe",
            @"D:\astap\astap_cli.exe",
        };

        foreach (var path in cliPaths)
        {
            if (File.Exists(path)) { _selectedExePath = path; _isCliVersion = true; return; }
        }

        var guiPaths = new List<string>
        {
            @"C:\Program Files\astap\astap.exe",
            @"C:\Program Files (x86)\astap\astap.exe",
            @"C:\astap\astap.exe",
            @"D:\Program Files\astap\astap.exe",
            @"D:\Program Files (x86)\astap\astap.exe",
            @"D:\astap\astap.exe",
        };

        foreach (var path in guiPaths)
        {
            if (File.Exists(path)) { _selectedExePath = path; _isCliVersion = false; return; }
        }
        _selectedExePath = null;
    }

    // Aggiunto parametro 'onLogReceived' per lo streaming in tempo reale
    public async Task<PlateSolvingResult> SolveAsync(string fitsFilePath, CancellationToken token = default, Action<string>? onLogReceived = null)
    {
        var result = new PlateSolvingResult();
        FindBestExecutable();

        if (string.IsNullOrEmpty(_selectedExePath)) 
        {
            result.Message = "ERRORE: ASTAP non trovato.";
            result.Success = false;
            return result;
        }

        var args = $"-f \"{fitsFilePath}\" -r 180 -update -z 0";
        
        var startInfo = new ProcessStartInfo
        {
            FileName = _selectedExePath,
            Arguments = args,
            CreateNoWindow = true,
        };

        if (_isCliVersion)
        {
            startInfo.UseShellExecute = false; 
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.WindowStyle = ProcessWindowStyle.Hidden;
        }
        else
        {
            startInfo.UseShellExecute = true;
            startInfo.WindowStyle = ProcessWindowStyle.Hidden;
            startInfo.RedirectStandardOutput = false;
            startInfo.RedirectStandardError = false;
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var tcs = new TaskCompletionSource<bool>();
        var logBuilder = new StringBuilder();

        // LOGICA STREAMING
        if (_isCliVersion)
        {
            process.OutputDataReceived += (s, e) => 
            { 
                if (e.Data != null) 
                {
                    // 1. Salviamo nel buffer interno per l'analisi finale
                    logBuilder.AppendLine(e.Data); 
                    // 2. SPEDIAMO ALLA UI SUBITO
                    onLogReceived?.Invoke(e.Data);
                } 
            };
            
            process.ErrorDataReceived += (s, e) => 
            { 
                if (e.Data != null) 
                {
                    string err = $"ERR: {e.Data}";
                    logBuilder.AppendLine(err);
                    onLogReceived?.Invoke(err);
                } 
            };
        }

        process.Exited += (sender, args) => tcs.TrySetResult(true);

        try
        {
            process.Start();
            
            if (_isCliVersion)
            {
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }

            var cancelTask = Task.Delay(-1, token);
            var timeoutTask = Task.Delay(90000, token); 
            var processTask = tcs.Task;

            var completedTask = await Task.WhenAny(processTask, timeoutTask, cancelTask);

            if (token.IsCancellationRequested)
            {
                result.Message = "Annullato dall'utente.";
                try { process.Kill(); } catch { }
                result.Success = false;
            }
            else if (completedTask == processTask)
            {
                // Analisi Successo
                string fullOutput = logBuilder.ToString();
                
                if (_isCliVersion)
                {
                    bool logSaysSuccess = fullOutput.Contains("Solution found", StringComparison.InvariantCultureIgnoreCase) 
                                       || fullOutput.Contains("PLTSOLVD=T", StringComparison.InvariantCultureIgnoreCase)
                                       || fullOutput.Contains("created wcs", StringComparison.InvariantCultureIgnoreCase);
                    
                    result.Success = logSaysSuccess;
                }
                else
                {
                    result.Success = process.ExitCode == 0;
                }
                result.Message = result.Success ? "OK" : "Nessuna soluzione trovata.";
            }
            else
            {
                result.Message = "Timeout";
                try { process.Kill(); } catch { }
                result.Success = false;
            }
        }
        catch (Exception ex)
        {
            result.Message = ex.Message;
            result.Success = false;
        }

        result.FullLog = logBuilder.ToString();
        return result;
    }
}