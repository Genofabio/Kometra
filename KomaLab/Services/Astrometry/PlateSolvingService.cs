using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace KomaLab.Services.Astrometry;

public class PlateSolvingResult
{
    public bool Success { get; set; }
    public string LogOutput { get; set; } = string.Empty;
}

public class PlateSolvingService
{
    /// <summary>
    /// Tenta di trovare il percorso dell'eseguibile di ASTAP automaticamente.
    /// </summary>
    private string? FindAstapExecutable()
    {
        // Lista dei percorsi comuni dove ASTAP potrebbe essere installato
        var possiblePaths = new[]
        {
            @"C:\Program Files\astap\astap.exe",
            @"C:\Program Files (x86)\astap\astap.exe",
            @"C:\astap\astap.exe",
            @"D:\Program Files\astap\astap.exe",
            @"D:\Program Files (x86)\astap\astap.exe",
            @"D:\astap\astap.exe",
            // Se l'hai installato altrove, AGGIUNGI QUI IL TUO PERCORSO:
            // @"D:\Astronomia\astap\astap.exe" 
        };

        foreach (var path in possiblePaths)
        {
            if (File.Exists(path)) return path;
        }

        return null; // Non trovato
    }

    /// <summary>
    /// Lancia ASTAP per risolvere l'immagine.
    /// Ritorna TRUE se ha avuto successo (e ha scritto il WCS nel file).
    /// </summary>
    public async Task<PlateSolvingResult> SolveAsync(string fitsFilePath)
    {
        var result = new PlateSolvingResult();
        var logBuilder = new StringBuilder();

        var astapPath = FindAstapExecutable();
        if (string.IsNullOrEmpty(astapPath)) 
        {
            result.LogOutput = "ERRORE: Eseguibile ASTAP non trovato.";
            result.Success = false;
            return result;
        }

        if (!File.Exists(fitsFilePath))
        {
            result.LogOutput = $"ERRORE: File non trovato: {fitsFilePath}";
            result.Success = false;
            return result;
        }

        // Parametri: -r 180 (Blind) -update (Scrivi Header) -z 0 (Auto downsample)
        var args = $"-f \"{fitsFilePath}\" -r 180 -update -z 0";

        logBuilder.AppendLine($"Avvio ASTAP su: {Path.GetFileName(fitsFilePath)}");
        
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = astapPath,
                Arguments = args,
                
                // --- CONFIGURAZIONE PER NASCONDERE IL PIÙ POSSIBILE ---
                
                // Fondamentale per leggere i log
                UseShellExecute = false, 
                
                // Mettiamo FALSE qui. Sembra controintuitivo, ma per le app GUI (non Console)
                // a volte "True" interferisce con il WindowStyle.
                CreateNoWindow = false, 
                
                // Forziamo lo stato "Nascosto"
                WindowStyle = ProcessWindowStyle.Hidden,
                
                // Opzionale: impedisce la creazione di finestre di errore di Windows
                ErrorDialog = false,
                // ------------------------------------------------------

                RedirectStandardOutput = true,
                RedirectStandardError = true
            },
            EnableRaisingEvents = true
        };

        // Cattura output in tempo reale
        process.OutputDataReceived += (s, e) => { if (e.Data != null) logBuilder.AppendLine(e.Data); };
        process.ErrorDataReceived += (s, e) => { if (e.Data != null) logBuilder.AppendLine($"ERR: {e.Data}"); };

        var tcs = new TaskCompletionSource<bool>();

        process.Exited += (sender, args) =>
        {
            tcs.TrySetResult(process.ExitCode == 0);
            process.Dispose();
        };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(60000)); // 60 sec timeout

            if (completedTask == tcs.Task)
            {
                result.Success = await tcs.Task;
                if (result.Success) logBuilder.AppendLine(">> SUCCESSO: Soluzione WCS scritta nel file.");
                else logBuilder.AppendLine(">> FALLITO: ASTAP non ha trovato una soluzione.");
            }
            else
            {
                logBuilder.AppendLine(">> TIMEOUT: Processo interrotto.");
                try { process.Kill(); } catch { }
                result.Success = false;
            }
        }
        catch (Exception ex)
        {
            logBuilder.AppendLine($">> ECCEZIONE: {ex.Message}");
            result.Success = false;
        }

        result.LogOutput = logBuilder.ToString();
        return result;
    }
}