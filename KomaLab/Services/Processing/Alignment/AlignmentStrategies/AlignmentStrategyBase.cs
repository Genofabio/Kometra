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
using KomaLab.Services.Processing.Engines;
using OpenCvSharp;

namespace KomaLab.Services.Processing.Alignment.AlignmentStrategies;

public abstract class AlignmentStrategyBase : IAlignmentStrategy
{
    protected readonly IFitsDataManager DataManager;
    
    // Dipendenza opzionale per l'analisi (le strategie che ne hanno bisogno la useranno)
    protected readonly IImageAnalysisEngine? AnalysisEngine; 

    protected AlignmentStrategyBase(IFitsDataManager dataManager, IImageAnalysisEngine? analysisEngine = null)
    {
        DataManager = dataManager ?? throw new ArgumentNullException(nameof(dataManager));
        AnalysisEngine = analysisEngine;
    }

    public abstract Task<Point2D?[]> CalculateAsync(
        IEnumerable<FitsFileReference> files,
        IEnumerable<Point2D?> guesses, 
        int searchRadius,
        IProgress<AlignmentProgressReport>? progress = null,
        CancellationToken token = default);

    // --- LOGICA DI CONCORRENZA ---
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

    // --- RESILIENZA ---
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
                    DataManager.ClearCache();
                    GC.Collect();
                    await Task.Delay(1000);
                }
                if (attempt < maxRetries) await Task.Delay(300 * attempt);
            }
        }
        return default;
    }
    
    // =======================================================================
    // HELPERS COMUNI PER LA SANITIZZAZIONE
    // =======================================================================

    /// <summary>
    /// Ritaglia l'immagine sui dati validi, espandendo se necessario per includere il guess.
    /// Sostituisce i NaN interni con 0.0.
    /// Gestisce la conversione tra Rect2D (Double) e OpenCV.Rect (Int).
    /// </summary>
    protected (Mat CroppedMat, Point2D Offset) SanitizeAndCrop(Mat src, Point2D? guess)
    {
        if (AnalysisEngine == null) throw new InvalidOperationException("AnalysisEngine richiesto per SanitizeAndCrop.");

        // 1. Trova il box dei dati validi (Rect2D -> Double)
        Rect2D rawBox = AnalysisEngine.FindValidDataBox(src);
        
        // Convertiamo in Rect OpenCV (Interi) per il cropping
        Rect validRect;

        // Se l'immagine è vuota o il box non valido, prendiamo tutto
        if (rawBox.Width <= 0 || rawBox.Height <= 0)
        {
            validRect = new Rect(0, 0, src.Width, src.Height);
        }
        else
        {
            // Casting esplicito a int perché OpenCV lavora su pixel interi
            validRect = new Rect((int)rawBox.X, (int)rawBox.Y, (int)rawBox.Width, (int)rawBox.Height);
        }

        // 2. Espansione dinamica per includere il guess (se fuori dal box valido)
        if (guess.HasValue)
        {
            int gx = (int)guess.Value.X;
            int gy = (int)guess.Value.Y;
            int margin = 50; 

            // Calcoli usando interi e Rect di OpenCV
            int minX = Math.Min(validRect.X, gx - margin);
            int minY = Math.Min(validRect.Y, gy - margin);
            
            // Nota: Rect2D non ha .Right/.Bottom, ma OpenCV Rect sì (.Right / .Bottom)
            // Oppure calcoliamo manualmente: X + Width
            int currentRight = validRect.X + validRect.Width;
            int currentBottom = validRect.Y + validRect.Height;

            int maxX = Math.Max(currentRight, gx + margin);
            int maxY = Math.Max(currentBottom, gy + margin);

            // Clamping ai bordi fisici
            minX = Math.Max(0, minX);
            minY = Math.Max(0, minY);
            maxX = Math.Min(src.Width, maxX);
            maxY = Math.Min(src.Height, maxY);

            // Aggiorniamo il rettangolo di taglio finale
            validRect = new Rect(minX, minY, maxX - minX, maxY - minY);
        }

        // 3. Crop (Il costruttore di Mat accetta OpenCvSharp.Rect)
        Mat cropped = new Mat(src, validRect);

        // 4. Patching NaN interni
        // Sostituisce eventuali pixel NaN inclusi nel crop (es. area nera) con 0.0
        Cv2.PatchNaNs(cropped, 0.0);

        return (cropped, new Point2D(validRect.X, validRect.Y));
    }

    /// <summary>
    /// Sostituisce i NaN con 0.0 in-place senza ritagliare. 
    /// Utile per FFT/Stelle dove le dimensioni devono restare fisse.
    /// </summary>
    protected void PatchNaNsInPlace(Mat src)
    {
        Cv2.PatchNaNs(src, 0.0);
    }
}