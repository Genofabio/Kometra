using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KomaLab.Models.Fits;
using KomaLab.Models.Primitives;
using KomaLab.Models.Processing;
using KomaLab.Services.Fits;

namespace KomaLab.Services.Processing.Alignment.AlignmentStrategies;

public abstract class AlignmentStrategyBase : IAlignmentStrategy
{
    protected readonly IFitsDataManager DataManager;

    protected AlignmentStrategyBase(IFitsDataManager dataManager)
    {
        DataManager = dataManager ?? throw new ArgumentNullException(nameof(dataManager));
    }

    // Firma aggiornata per accogliere i guesses esterni
    public abstract Task<Point2D?[]> CalculateAsync(
        IEnumerable<FitsFileReference> files,
        IEnumerable<Point2D?> guesses, 
        int searchRadius,
        IProgress<AlignmentProgressReport>? progress = null,
        CancellationToken token = default);

    // --- LOGICA DI CONCORRENZA (Invariata, è già ottima) ---
    protected int GetOptimalConcurrency(FitsFileReference sampleFile)
    {
        long fileSize = 0;
        try { if (File.Exists(sampleFile.FilePath)) fileSize = new FileInfo(sampleFile.FilePath).Length; } 
        catch { return 1; }

        long estimatedMemoryPerImage = fileSize * 8; 
        var gcInfo = GC.GetGCMemoryInfo();
        long availableMemory = gcInfo.TotalAvailableMemoryBytes > 0 ? gcInfo.TotalAvailableMemoryBytes : 1024L * 1024 * 1024 * 2; 

        long safeMemoryBudget = (long)(availableMemory * 0.6);
        int maxImagesInMemory = (int)(safeMemoryBudget / Math.Max(1, estimatedMemoryPerImage));
        int optimal = Math.Clamp(maxImagesInMemory, 1, Math.Min(Environment.ProcessorCount, 4));
        
        return fileSize > 150 * 1024 * 1024 ? 1 : optimal;
    }

    // --- RESILIENZA (Invariata, con ClearCache corretto) ---
    protected async Task<T?> ExecuteWithRetryAsync<T>(
        Func<Task<T?>> operation, 
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
                if (result != null) return result;
                if (attempt < maxRetries) await Task.Delay(150 * attempt);
            }
            catch (Exception ex)
            {
                if (ex is OutOfMemoryException)
                {
                    DataManager.ClearCache(); // Libera solo la RAM, non il disco!
                    GC.Collect();
                    await Task.Delay(1000);
                }
                if (attempt < maxRetries) await Task.Delay(300 * attempt);
            }
        }
        return default;
    }
}