using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kometra.Models.Fits;
using Kometra.Models.Primitives;
using Kometra.Models.Processing;
using Kometra.Models.Processing.Alignment;
using Kometra.Services.Fits;
using Kometra.Services.Processing.Engines;
using OpenCvSharp;

namespace Kometra.Services.Processing.Alignment.AlignmentStrategies;

public abstract class AlignmentStrategyBase : IAlignmentStrategy
{
    protected readonly IFitsDataManager DataManager;
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
                // Se fallisce all'ultimo tentativo, l'eccezione risale
                if (attempt == maxRetries) throw; 
            }
        }
        return default;
    }
    
    // =======================================================================
    // HELPERS COMUNI CON FIX PER DOUBLE (64-BIT)
    // =======================================================================

    protected (Mat CroppedMat, Point2D Offset) SanitizeAndCrop(Mat src, Point2D? guess)
    {
        if (AnalysisEngine == null) throw new InvalidOperationException("AnalysisEngine richiesto per SanitizeAndCrop.");

        // 1. Trova il box dei dati validi
        Rect2D rawBox = AnalysisEngine.FindValidDataBox(src);
        
        Rect validRect;
        if (rawBox.Width <= 0 || rawBox.Height <= 0)
        {
            validRect = new Rect(0, 0, src.Width, src.Height);
        }
        else
        {
            validRect = new Rect((int)rawBox.X, (int)rawBox.Y, (int)rawBox.Width, (int)rawBox.Height);
        }

        // 2. Espansione dinamica per includere il guess
        if (guess.HasValue)
        {
            int gx = (int)guess.Value.X;
            int gy = (int)guess.Value.Y;
            int margin = 50; 

            int minX = Math.Min(validRect.X, gx - margin);
            int minY = Math.Min(validRect.Y, gy - margin);
            int currentRight = validRect.X + validRect.Width;
            int currentBottom = validRect.Y + validRect.Height;
            int maxX = Math.Max(currentRight, gx + margin);
            int maxY = Math.Max(currentBottom, gy + margin);

            minX = Math.Max(0, minX);
            minY = Math.Max(0, minY);
            maxX = Math.Min(src.Width, maxX);
            maxY = Math.Min(src.Height, maxY);

            validRect = new Rect(minX, minY, maxX - minX, maxY - minY);
        }

        // 3. Crop
        Mat cropped = new Mat(src, validRect);

        // 4. FIX PATCHING: Supporto per Double (64-bit)
        SafePatchNaNs(cropped, 0.0);

        return (cropped, new Point2D(validRect.X, validRect.Y));
    }

    protected void PatchNaNsInPlace(Mat src)
    {
        // FIX PATCHING: Supporto per Double (64-bit)
        SafePatchNaNs(src, 0.0);
    }

    /// <summary>
    /// Sostituisce i NaN con 'val' gestendo sia Float (32) che Double (64).
    /// Cv2.PatchNaNs supporta solo CV_32F, quindi implementiamo un workaround per CV_64F.
    /// </summary>
    private void SafePatchNaNs(Mat mat, double val)
    {
        if (mat.Empty()) return;

        if (mat.Depth() == MatType.CV_32F)
        {
            // Per Float 32-bit, usiamo la funzione nativa veloce
            Cv2.PatchNaNs(mat, val);
        }
        else if (mat.Depth() == MatType.CV_64F)
        {
            // Per Double 64-bit, usiamo il confronto logico (NaN != NaN è true)
            // 1. Creiamo una maschera dove i pixel sono NaN
            using var mask = new Mat();
            // Confrontando la matrice con se stessa usando NotEqual, 
            // solo i NaN risulteranno True (255) perché (NaN != NaN)
            Cv2.Compare(mat, mat, mask, CmpType.NE);
            
            // 2. Impostiamo a 'val' i pixel identificati dalla maschera
            // Se la maschera è vuota (countNonZero == 0), SetTo è quasi istantaneo
            if (Cv2.CountNonZero(mask) > 0)
            {
                mat.SetTo(new Scalar(val), mask);
            }
        }
        // Ignoriamo altri tipi (Int, Byte) che non possono contenere NaN
    }
}