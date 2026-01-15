using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using KomaLab.Models.Primitives;
using KomaLab.Models.Processing; 
using KomaLab.Services.Fits;
using OpenCvSharp;

namespace KomaLab.Services.Processing.AlignmentStrategies;

// ---------------------------------------------------------------------------
// FILE: ManualCometAlignmentStrategy.cs
// RUOLO: Strategia Allineamento Manuale (Raffinamento ROI)
// VERSIONE: Aggiornata per Architettura No-FitsImageData
// ---------------------------------------------------------------------------

public class ManualCometAlignmentStrategy : AlignmentStrategyBase
{
    private readonly IFitsIoService _ioService;
    private readonly IFitsOpenCvConverter _converter;
    private readonly IImageAnalysisService _analysis;
    private readonly CenteringMethod _method; 

    public ManualCometAlignmentStrategy(
        IFitsIoService ioService, 
        IFitsOpenCvConverter converter, 
        IImageAnalysisService analysis,
        CenteringMethod method)
    {
        _ioService = ioService ?? throw new ArgumentNullException(nameof(ioService));
        _converter = converter ?? throw new ArgumentNullException(nameof(converter));
        _analysis = analysis ?? throw new ArgumentNullException(nameof(analysis));
        _method = method;
    }

    public override async Task<Point2D?[]> CalculateAsync(
        List<string> sourcePaths, 
        List<Point2D?> guesses, 
        int searchRadius, 
        IProgress<(int Index, Point2D? Center)>? progress)
    {
        int n = sourcePaths.Count;
        var results = new Point2D?[n];

        // 1. Concorrenza ottimizzata
        int maxConcurrency = GetOptimalConcurrency(sourcePaths.Count > 0 ? sourcePaths[0] : "");
        using var semaphore = new SemaphoreSlim(maxConcurrency);
        var tasks = new List<Task>();

        for (int i = 0; i < n; i++)
        {
            int index = i;
            string path = sourcePaths[index];
            
            // In manuale, se manca il guess (click utente), saltiamo il frame.
            var guess = (index < guesses.Count) ? guesses[index] : null;

            if (guess == null) 
            {
                results[index] = null;
                continue; 
            }

            tasks.Add(Task.Run(async () =>
            {
                await semaphore.WaitAsync();
                try
                {
                    // OTTIMIZZAZIONE "FAST PATH":
                    // Se il raggio è <= 0, fidiamoci ciecamente del click utente.
                    // Evitiamo caricamento e calcoli.
                    if (searchRadius <= 0)
                    {
                        results[index] = guess;
                        progress?.Report((index, guess));
                        return;
                    }

                    // RETRY LOGIC:
                    // Se dobbiamo raffinare il centro, carichiamo il file protetto da retry.
                    Point2D? result = await ExecuteWithRetryAsync(
                        operation: async () => await RefineCenterCoreAsync(path, guess.Value, searchRadius),
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
    /// Logica Core: Carica file -> Ritaglia ROI -> Trova Centroide.
    /// Ritorna null SOLO per errori di I/O (per scatenare il retry).
    /// </summary>
    private async Task<Point2D?> RefineCenterCoreAsync(string path, Point2D guess, int radius)
    {
        try
        {
            // 1. ADATTAMENTO ARCHITETTURA: Caricamento Separato
            var header = await _ioService.ReadHeaderAsync(path);
            var pixels = await _ioService.ReadPixelDataAsync(path);
            
            if (header == null || pixels == null) return null; // Trigger Retry

            // Estrazione parametri scala
            double bScale = header.GetValue<double>("BSCALE") ?? 1.0;
            double bZero = header.GetValue<double>("BZERO") ?? 0.0;

            using var fullMat = _converter.RawToMat(pixels, bScale, bZero);

            // 2. Calcolo Coordinate ROI (Region Of Interest)
            int size = radius * 2;
            int x = (int)(guess.X - radius);
            int y = (int)(guess.Y - radius);
            
            // Clipping sicuro sui bordi dell'immagine
            int roiX = Math.Max(0, x);
            int roiY = Math.Max(0, y);
            int roiW = Math.Min(fullMat.Width, x + size) - roiX;
            int roiH = Math.Min(fullMat.Height, y + size) - roiY;

            // Se la ROI è degenere (troppo piccola o fuori immagine), ritorniamo il guess.
            if (roiW <= 4 || roiH <= 4) return guess;

            // 3. Analisi Locale
            using var roiMat = new Mat(fullMat, new OpenCvSharp.Rect(roiX, roiY, roiW, roiH));
            
            Point2D localCenter;
            switch (_method)
            {
                case CenteringMethod.Centroid: 
                    localCenter = _analysis.FindCentroid(roiMat); 
                    break;
                case CenteringMethod.GaussianFit: 
                    localCenter = _analysis.FindGaussianCenter(roiMat); 
                    break;
                case CenteringMethod.Peak: 
                    localCenter = _analysis.FindPeak(roiMat); 
                    break;
                default: 
                    localCenter = _analysis.FindCenterOfLocalRegion(roiMat); 
                    break;
            }
            
            // 4. Trasformazione Coordinate (Locale -> Globale)
            return new Point2D(localCenter.X + roiX, localCenter.Y + roiY);
        }
        catch (Exception)
        {
            // Se fallisce la matematica (es. ROI corrotta), non l'I/O, usiamo il guess.
            return guess;
        }
    }
}