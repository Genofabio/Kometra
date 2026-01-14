using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using KomaLab.Models.Primitives;

namespace KomaLab.Services.Processing.AlignmentStrategies;

// ---------------------------------------------------------------------------
// FILE: AlignmentStrategyBase.cs
// RUOLO: Classe Base Astratta
// DESCRIZIONE:
// Fornisce le funzionalità infrastrutturali (Retry, Concorrenza) a tutte le strategie.
// ---------------------------------------------------------------------------

public abstract class AlignmentStrategyBase : IAlignmentStrategy
{
    // Contratto che le classi figlie devono implementare
    public abstract Task<Point2D?[]> CalculateAsync(
        List<string> sourcePaths,
        List<Point2D?> guesses,
        int searchRadius,
        IProgress<(int Index, Point2D? Center)>? progress);

    // --- HELPER 1: Calcolo Concorrenza ---
    // --- HELPER 1: Calcolo Concorrenza (ROBUSTO) ---
    protected int GetOptimalConcurrency(string firstFilePath)
    {
        long fileSize = 0;
        try 
        { 
            if (File.Exists(firstFilePath)) 
                fileSize = new FileInfo(firstFilePath).Length; 
        } 
        catch { return 1; }

        // Stima dell'impronta in memoria (Memory Footprint) per una singola immagine.
        // Un file FITS da 50MB (16-bit) diventa 200MB in Double (64-bit).
        // OpenCV alloca buffer aggiuntivi per le operazioni (blur, threshold).
        // Moltiplichiamo x6 per stare sicuri (Raw + Mat Double + Buffer Analisi + Overhead .NET).
        long estimatedMemoryPerImage = fileSize * 6;

        // Recuperiamo la memoria disponibile del sistema
        var gcInfo = GC.GetGCMemoryInfo();
        long availableMemory = gcInfo.TotalAvailableMemoryBytes;
        
        // Se non riusciamo a leggere la memoria disponibile (es. container limitati), usiamo un default sicuro.
        if (availableMemory <= 0) availableMemory = 1024L * 1024 * 1024 * 2; // Assumiamo 2GB liberi per sicurezza

        // Quante immagini possiamo tenere in memoria contemporaneamente lasciando il 30% libero?
        long safeMemoryBudget = (long)(availableMemory * 0.7);
        int maxImagesInMemory = (int)(safeMemoryBudget / Math.Max(1, estimatedMemoryPerImage));

        // Logica di clamp finale
        int cpuLimit = Environment.ProcessorCount;
        
        // Regola d'oro: Mai più di 4 thread per l'elaborazione immagini pesanti, anche se hai 32 core.
        // Il collo di bottiglia diventa la banda della memoria RAM, non la CPU.
        int hardCap = 4; 

        int optimalConcurrency = Math.Clamp(maxImagesInMemory, 1, hardCap);
        
        // Se l'immagine è gigante (>100MB su disco -> ~600MB RAM), forza 1 thread singolo.
        if (fileSize > 100 * 1024 * 1024) optimalConcurrency = 1;

        Debug.WriteLine($"[StrategyBase] File: {fileSize/1024/1024}MB. Est.Mem/Img: {estimatedMemoryPerImage/1024/1024}MB. Concurrency: {optimalConcurrency}");

        return optimalConcurrency;
    }

    // --- HELPER 2: Esecuzione con Retry (Resilienza I/O) ---
    /// <summary>
    /// Esegue una funzione asincrona con tentativi multipli in caso di errore o risultato nullo.
    /// </summary>
    protected async Task<T?> ExecuteWithRetryAsync<T>(
        Func<Task<T?>> operation, 
        T? fallbackValue, 
        int itemIndex, 
        int maxRetries = 3)
    {
        int attempt = 0;
        while (attempt < maxRetries)
        {
            attempt++;
            try
            {
                T? result = await operation();

                // Se il risultato è valido, lo ritorniamo subito
                if (result != null) return result;

                // Se è null, potrebbe essere un errore transitorio (es. disco occupato), riproviamo
                if (attempt < maxRetries)
                {
                    Debug.WriteLine($"[StrategyBase] Item {itemIndex}: Tentativo {attempt} nullo. Riprovo...");
                    await Task.Delay(100 * attempt); // Backoff: 100ms, 200ms...
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[StrategyBase] Item {itemIndex}: Errore {ex.GetType().Name} al tentativo {attempt}. {ex.Message}");
                
                // Se l'errore è di memoria, forziamo una pulizia aggressiva prima di riprovare
                if (ex is OutOfMemoryException)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    await Task.Delay(1000); // Pausa lunga per far respirare il sistema
                }
                
                if (attempt < maxRetries) 
                {
                    await Task.Delay(200 * attempt);
                }
            }
        }

        Debug.WriteLine($"[StrategyBase] Item {itemIndex}: Fallito dopo {maxRetries} tentativi.");
        return fallbackValue;
    }
}