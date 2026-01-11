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
// RUOLO: Wrapper Processo Esterno
// DESCRIZIONE:
// Gestisce l'interazione con il motore di risoluzione astrometrica ASTAP.
// Si occupa di:
// 1. Trovare l'eseguibile (GUI o CLI) nel sistema.
// 2. Lanciare il processo con i parametri corretti (-update per scrivere WCS).
// 3. Monitorare l'esecuzione (timeout, cancellazione).
// 4. Interpretare l'output per determinare il successo.
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
        // -z 0: downsample factor (0 = auto, migliora velocità su immagini grandi)
        // -fov 0: campo visivo (0 = auto/da header)
        var args = $"-f \"{fitsFilePath}\" -r 180 -update -z 0 -fov 0";
        
        var startInfo = new ProcessStartInfo
        {
            FileName = _cachedExePath,
            Arguments = args,
            CreateNoWindow = true,
            // UseShellExecute deve essere FALSE per ridirigere l'output (necessario per la versione CLI)
            // Se è la versione GUI, non possiamo ridirigere facilmente stdout, ma ASTAP GUI scrive log su file.
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
        process.EnableRaisingEvents = true; // Necessario per l'evento Exited
        
        var logBuilder = new StringBuilder();

        // Collega i gestori di eventi per catturare l'output in tempo reale
        SetupLogRedirection(process, logBuilder, onLogReceived);

        var tcs = new TaskCompletionSource<bool>();
        
        // 3. Gestione Cancellazione
        // Registra una callback sul token che annulla il TaskCompletionSource
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

            // 4. Attesa Esecuzione con Timeout
            // ASTAP può impiegare tempo per blind solve, diamo 90 secondi.
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(90), token);
            var processTask = tcs.Task;

            var completedTask = await Task.WhenAny(processTask, timeoutTask);

            if (completedTask == timeoutTask || token.IsCancellationRequested)
            {
                // Timeout o Annullamento Manuale
                KillProcessSafe(process);
                result.Success = false;
                result.Message = token.IsCancellationRequested ? "Operazione annullata dall'utente." : "Timeout operazione (90s).";
            }
            else
            {
                // Processo terminato naturalmente
                // Aspettiamo un istante per assicurarci che i buffer di output siano flushati
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

    private void EnsureExecutableFound()
    {
        if (_hasSearched) return;

        var searchPaths = new List<string>();
        
        void AddVariations(string basePath)
        {
            if (string.IsNullOrEmpty(basePath)) return;
            // Ordine preferenza: CLI (più verboso/facile da integrare) -> GUI
            searchPaths.Add(Path.Combine(basePath, "astap", "astap_cli.exe"));
            searchPaths.Add(Path.Combine(basePath, "astap", "astap.exe"));
        }

        // Percorsi standard Windows
        AddVariations(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        AddVariations(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        
        // Percorsi "Hardcoded" comuni (molti astrofili installano in root C:\astap)
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
                break; // Trovato il primo valido
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
                // Non sempre stderr di ASTAP è un errore fatale, spesso sono info di debug
                uiCallback?.Invoke(err);
            } 
        };
    }

    private bool EvaluateSuccess(Process process, string output)
    {
        // Logica di validazione del successo
        if (_isCliVersion)
        {
            // La CLI scrive esplicitamente messaggi di successo
            return output.Contains("Solution found", StringComparison.InvariantCultureIgnoreCase) 
                || output.Contains("PLTSOLVD=T", StringComparison.InvariantCultureIgnoreCase)
                || output.Contains("created wcs", StringComparison.InvariantCultureIgnoreCase)
                || output.Contains("Updating header", StringComparison.InvariantCultureIgnoreCase);
        }
        else
        {
            // La versione GUI è "muta" se lanciata così, ci affidiamo all'ExitCode.
            // ASTAP GUI solitamente ritorna 0 se ok, o diverso se errore.
            // Una verifica più robusta (da implementare nel chiamante) è controllare
            // se il file FITS ha una data di modifica recente o nuove chiavi WCS.
            return process.ExitCode == 0;
        }
    }

    private void KillProcessSafe(Process p)
    {
        try 
        { 
            if (!p.HasExited) p.Kill(); 
        } 
        catch 
        { 
            // Ignora race conditions (il processo potrebbe essersi chiuso mentre tentavamo di ucciderlo) 
        }
    }
}