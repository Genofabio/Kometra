using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using KomaLab.Models.Primitives;
using KomaLab.Services.Fits;
using OpenCvSharp;

namespace KomaLab.Services.Processing.AlignmentStrategies;

// ---------------------------------------------------------------------------
// FILE: AutomaticCometAlignmentStrategy.cs
// RUOLO: Strategia Allineamento Automatico (Smart ROI / Blind)
// VERSIONE: Aggiornata per Architettura No-FitsImageData
// ---------------------------------------------------------------------------

public class AutomaticCometAlignmentStrategy : AlignmentStrategyBase
{
    private readonly IFitsIoService _ioService;
    private readonly IFitsOpenCvConverter _converter;
    private readonly IImageAnalysisService _analysis;

    public AutomaticCometAlignmentStrategy(
        IFitsIoService ioService, 
        IFitsOpenCvConverter converter, 
        IImageAnalysisService analysis)
    {
        _ioService = ioService ?? throw new ArgumentNullException(nameof(ioService));
        _converter = converter ?? throw new ArgumentNullException(nameof(converter));
        _analysis = analysis ?? throw new ArgumentNullException(nameof(analysis));
    }

    public override async Task<Point2D?[]> CalculateAsync(
        List<string> sourcePaths, 
        List<Point2D?> guesses, 
        int searchRadius, 
        IProgress<(int Index, Point2D? Center)>? progress)
    {
        int n = sourcePaths.Count;
        var results = new Point2D?[n];

        Debug.WriteLine($"[AutoStrategy] Start Batch. Files: {n}, Guesses Present: {guesses != null && guesses.Count > 0}");

        // 1. Concorrenza (Invariato)
        int maxConcurrency = GetOptimalConcurrency(sourcePaths.Count > 0 ? sourcePaths[0] : "");
        using var semaphore = new SemaphoreSlim(maxConcurrency);
        var tasks = new List<Task>();

        for (int i = 0; i < n; i++)
        {
            int index = i;
            string path = sourcePaths[index];
            
            Point2D? guess = (guesses != null && index < guesses.Count) ? guesses[index] : null;

            tasks.Add(Task.Run(async () =>
            {
                await semaphore.WaitAsync();
                try
                {
                    // 2. Esecuzione protetta da Retry
                    Point2D? result = await ExecuteWithRetryAsync(
                        operation: async () => await FindObjectCoreAsync(path, guess, index),
                        fallbackValue: guess, 
                        itemIndex: index
                    );
                    
                    results[index] = result;
                    progress?.Report((index, result));
                }
                finally { semaphore.Release(); }
            }));
        }

        await Task.WhenAll(tasks);
        return results;
    }

    /// <summary>
    /// Logica di business pura per una singola immagine.
    /// </summary>
    private async Task<Point2D?> FindObjectCoreAsync(string path, Point2D? guess, int imgIndex)
    {
        // ADATTAMENTO ARCHITETTURA: Caricamento separato
        var header = await _ioService.ReadHeaderAsync(path);
        var rawPixels = await _ioService.ReadPixelDataAsync(path);

        if (header == null || rawPixels == null) return null; 

        // Estrazione parametri scaling
        double bScale = header.GetValue<double>("BSCALE") ?? 1.0;
        double bZero = header.GetValue<double>("BZERO") ?? 0.0;

        // Conversione Matrice
        using var mat = _converter.RawToMat(rawPixels, bScale, bZero);

        // --- LOGICA CORE (INVARIATA) ---

        // CASO A: ABBIAMO UN GUESS (Smart ROI Mode)
        if (guess.HasValue)
        {
            Debug.WriteLine($"[AutoStrategy] Image {imgIndex}: MODE SMART ROI (Guess: {guess})");

            int smartRadius = EstimateSmartRadius(mat, imgIndex);

            int size = smartRadius * 2;
            int x = (int)(guess.Value.X - smartRadius);
            int y = (int)(guess.Value.Y - smartRadius);

            var roiRect = new OpenCvSharp.Rect(x, y, size, size)
                .Intersect(new OpenCvSharp.Rect(0, 0, mat.Width, mat.Height));

            if (roiRect.Width > 4 && roiRect.Height > 4)
            {
                using var crop = new Mat(mat, roiRect);
                var localCenter = _analysis.FindCenterOfLocalRegion(crop);
                
                return new Point2D(localCenter.X + roiRect.X, localCenter.Y + roiRect.Y);
            }
            else
            {
                Debug.WriteLine($"[AutoStrategy] Image {imgIndex}: ROI Invalid. Using Guess.");
            }

            return guess;
        }

        // CASO B: NESSUN GUESS (Blind Mode)
        Debug.WriteLine($"[AutoStrategy] Image {imgIndex}: MODE BLIND (Full Scan)");
        return _analysis.FindCenterOfLocalRegion(mat);
    }

    /// <summary>
    /// Calcola la densità stellare per decidere quanto stringere il raggio di ricerca.
    /// (Logica Invariata)
    /// </summary>
    private int EstimateSmartRadius(Mat mat, int imgIndex)
    {
        int minDimension = Math.Min(mat.Width, mat.Height);
        int baseRadius = minDimension / 16; 

        Cv2.MeanStdDev(mat, out Scalar mean, out Scalar stddev);
        double meanVal = mean.Val0;
        double stdVal = stddev.Val0;
        
        double thresholdVal = meanVal + (5 * stdVal);

        using var threshMask = new Mat();
        Cv2.Threshold(mat, threshMask, thresholdVal, 255, ThresholdTypes.Binary);
        int brightPixelCount = Cv2.CountNonZero(threshMask);

        double totalPixels = mat.Width * mat.Height;
        double density = brightPixelCount / totalPixels;

        double crowdingFactor = 1.0;
        
        if (density > 0.001) crowdingFactor = 0.4; 
        else if (density > 0.0002) crowdingFactor = 0.7;

        int finalRadius = (int)(baseRadius * crowdingFactor);
        finalRadius = Math.Max(30, finalRadius); 

        return finalRadius;
    }
}