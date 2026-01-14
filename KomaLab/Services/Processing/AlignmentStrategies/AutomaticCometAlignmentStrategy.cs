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
// AGGIORNAMENTO: Refactoring con AlignmentStrategyBase per resilienza (Retry).
// ---------------------------------------------------------------------------

public class AutomaticCometAlignmentStrategy : AlignmentStrategyBase
{
    private readonly IFitsIoService _ioService;
    private readonly IFitsImageDataConverter _converter;
    private readonly IImageAnalysisService _analysis;

    public AutomaticCometAlignmentStrategy(
        IFitsIoService ioService, 
        IFitsImageDataConverter converter, 
        IImageAnalysisService analysis)
    {
        _ioService = ioService ?? throw new ArgumentNullException(nameof(ioService));
        _converter = converter ?? throw new ArgumentNullException(nameof(converter));
        _analysis = analysis ?? throw new ArgumentNullException(nameof(analysis));
    }

    public override async Task<Point2D?[]> CalculateAsync(
        List<string> sourcePaths, 
        List<Point2D?> guesses, 
        int searchRadius, // Parametro ignorato in automatico (calcolato internamente)
        IProgress<(int Index, Point2D? Center)>? progress)
    {
        int n = sourcePaths.Count;
        var results = new Point2D?[n];

        Debug.WriteLine($"[AutoStrategy] Start Batch. Files: {n}, Guesses Present: {guesses != null && guesses.Count > 0}");

        // 1. Otteniamo la concorrenza ottimale dalla classe base
        int maxConcurrency = GetOptimalConcurrency(sourcePaths.Count > 0 ? sourcePaths[0] : "");
        using var semaphore = new SemaphoreSlim(maxConcurrency);
        var tasks = new List<Task>();

        for (int i = 0; i < n; i++)
        {
            int index = i;
            string path = sourcePaths[index];
            
            // Se la lista guesses è null o contiene null, attiverà la "Blind Mode"
            Point2D? guess = (guesses != null && index < guesses.Count) ? guesses[index] : null;

            tasks.Add(Task.Run(async () =>
            {
                await semaphore.WaitAsync();
                try
                {
                    // 2. Eseguiamo la logica Core protetta dal RETRY della classe base
                    Point2D? result = await ExecuteWithRetryAsync(
                        operation: async () => await FindObjectCoreAsync(path, guess, index),
                        fallbackValue: guess, // Se fallisce dopo N tentativi, restituiamo il guess (o null)
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
    /// Non gestisce retry o eccezioni I/O (gestite dal wrapper ExecuteWithRetryAsync).
    /// </summary>
    private async Task<Point2D?> FindObjectCoreAsync(string path, Point2D? guess, int imgIndex)
    {
        // Caricamento dati (se ritorna null per file lock, il wrapper riproverà)
        var fitsData = await _ioService.LoadAsync(path);
        if (fitsData == null) return null; 

        using var mat = _converter.RawToMat(fitsData);

        // --- CASO A: ABBIAMO UN GUESS (Smart ROI Mode) ---
        if (guess.HasValue)
        {
            Debug.WriteLine($"[AutoStrategy] Image {imgIndex}: MODE SMART ROI (Guess: {guess})");

            // Calcolo raggio adattivo basato sulla densità stellare
            int smartRadius = EstimateSmartRadius(mat, imgIndex);

            // Definizione ROI
            int size = smartRadius * 2;
            int x = (int)(guess.Value.X - smartRadius);
            int y = (int)(guess.Value.Y - smartRadius);

            var roiRect = new OpenCvSharp.Rect(x, y, size, size)
                .Intersect(new OpenCvSharp.Rect(0, 0, mat.Width, mat.Height));

            // Analisi Locale nella ROI
            if (roiRect.Width > 4 && roiRect.Height > 4)
            {
                using var crop = new Mat(mat, roiRect);
                var localCenter = _analysis.FindCenterOfLocalRegion(crop);
                
                return new Point2D(localCenter.X + roiRect.X, localCenter.Y + roiRect.Y);
            }
            else
            {
                Debug.WriteLine($"[AutoStrategy] Image {imgIndex}: ROI Invalid (Too small/Out of bounds). Using Guess.");
            }

            // Fallback al guess se la ROI è geometricamente invalida o nera
            return guess;
        }

        // --- CASO B: NESSUN GUESS (Blind Mode) ---
        Debug.WriteLine($"[AutoStrategy] Image {imgIndex}: MODE BLIND (Full Scan)");
        return _analysis.FindCenterOfLocalRegion(mat);
    }

    /// <summary>
    /// Calcola la densità stellare per decidere quanto stringere il raggio di ricerca.
    /// </summary>
    private int EstimateSmartRadius(Mat mat, int imgIndex)
    {
        int minDimension = Math.Min(mat.Width, mat.Height);
        int baseRadius = minDimension / 16; 

        // Analisi Statistica Rapida
        Cv2.MeanStdDev(mat, out Scalar mean, out Scalar stddev);
        double meanVal = mean.Val0;
        double stdVal = stddev.Val0;
        
        // Soglia alta per contare le stelle luminose (Mean + 5 Sigma)
        double thresholdVal = meanVal + (5 * stdVal);

        using var threshMask = new Mat();
        Cv2.Threshold(mat, threshMask, thresholdVal, 255, ThresholdTypes.Binary);
        int brightPixelCount = Cv2.CountNonZero(threshMask);

        double totalPixels = mat.Width * mat.Height;
        double density = brightPixelCount / totalPixels;

        // Decisione Fattore di Correzione
        double crowdingFactor = 1.0;
        
        if (density > 0.001) // Campo molto affollato
        {
            crowdingFactor = 0.4; 
        }
        else if (density > 0.0002) // Campo medio
        {
            crowdingFactor = 0.7;
        }
        // else: Campo vuoto -> fattore 1.0 (usa tutto il raggio base)

        int finalRadius = (int)(baseRadius * crowdingFactor);
        finalRadius = Math.Max(30, finalRadius); // Hard cap minimo di sicurezza

        Debug.WriteLine($"[AutoStrategy] Image {imgIndex}: Density={density:F5}, Factor={crowdingFactor}, Radius={finalRadius}px");

        return finalRadius;
    }
}