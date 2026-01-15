using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KomaLab.Models.Primitives;
using KomaLab.Services.Fits;
using OpenCvSharp;

namespace KomaLab.Services.Processing.AlignmentStrategies;

// ---------------------------------------------------------------------------
// FILE: StarAlignmentStrategy.cs
// RUOLO: Strategia Allineamento (Deep Sky)
// VERSIONE: Aggiornata per Architettura No-FitsImageData
// ---------------------------------------------------------------------------

public class StarAlignmentStrategy : AlignmentStrategyBase
{
    private readonly IFitsIoService _ioService;
    private readonly IFitsOpenCvConverter _converter;
    private readonly IImageAnalysisService _analysis;

    public StarAlignmentStrategy(
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

        // Rilevamento Modalità: Se il primo frame ha un guess, assumiamo priorità WCS
        bool hasWcsInput = (guesses.Count > 0 && guesses[0].HasValue);

        if (hasWcsInput)
        {
            await RunWcsPriorityModeAsync(sourcePaths, guesses, results, progress);
        }
        else
        {
            await RunVisualBlindModeAsync(sourcePaths, results, progress);
        }

        return results;
    }

    // =======================================================================
    // MODALITÀ 1: WCS PRIORITARIO
    // =======================================================================
    private async Task RunWcsPriorityModeAsync(
        List<string> sourcePaths, 
        List<Point2D?> guesses, 
        Point2D?[] results, 
        IProgress<(int Index, Point2D? Center)>? progress)
    {
        results[0] = guesses[0];
        progress?.Report((0, results[0]));

        for (int i = 1; i < sourcePaths.Count; i++)
        {
            // CASO A: Dato WCS presente -> Usalo (Veloce)
            if (i < guesses.Count && guesses[i].HasValue)
            {
                results[i] = guesses[i];
            }
            // CASO B: Buco nei dati WCS -> Fallback FFT sul frame precedente
            else
            {
                // Caricamento on-demand di due file per il calcolo shift
                Point2D shift = await CalculateShiftWithRetryAsync(sourcePaths[i - 1], sourcePaths[i], i);
                
                Point2D prevCenter = results[i - 1] ?? new Point2D(0, 0);
                results[i] = new Point2D(prevCenter.X + shift.X, prevCenter.Y + shift.Y);
            }
            progress?.Report((i, results[i]));
        }
    }

    // =======================================================================
    // MODALITÀ 2: FFT PURA (VISUALE / BLIND) - SEQUENZIALE
    // =======================================================================
    private async Task RunVisualBlindModeAsync(
        List<string> sourcePaths, 
        Point2D?[] results, 
        IProgress<(int Index, Point2D? Center)>? progress)
    {
        // 1. Inizializzazione Frame 0 (Centro geometrico)
        Point2D center0 = await ExecuteWithRetryAsync(async () => 
        {
            // ADATTAMENTO: ReadHeaderAsync (ex ReadHeaderOnlyAsync)
            var header = await _ioService.ReadHeaderAsync(sourcePaths[0]);
            if (header == null) return null;
            return (Point2D?)new Point2D(header.GetIntValue("NAXIS1") / 2.0, header.GetIntValue("NAXIS2") / 2.0);
        }, new Point2D(0,0), 0) ?? new Point2D(0,0);

        results[0] = center0;
        progress?.Report((0, center0));

        // 2. Caricamento Iniziale Matrice 0
        Mat? prevMat = await LoadMatWithRetryAsync(sourcePaths[0], 0);
        
        if (prevMat == null) return; 

        try
        {
            // Loop Sequenziale: Frame N vs Frame N-1
            for (int i = 1; i < sourcePaths.Count; i++)
            {
                Mat? currentMat = await LoadMatWithRetryAsync(sourcePaths[i], i);

                if (currentMat != null)
                {
                    Point2D shift = await Task.Run(() => _analysis.ComputeStarFieldShift(prevMat, currentMat));

                    Point2D prevCenter = results[i - 1]!.Value;
                    results[i] = new Point2D(prevCenter.X + shift.X, prevCenter.Y + shift.Y);

                    // --- SWAP EFFICIENTE ---
                    prevMat.Dispose();      
                    prevMat = currentMat;   
                    currentMat = null;      
                }
                else
                {
                    // Fallimento caricamento: Manteniamo posizione stabile
                    results[i] = results[i - 1];
                }

                progress?.Report((i, results[i]));
            }
        }
        finally
        {
            prevMat?.Dispose();
        }
    }

    // --- HELPERS PROTETTI DA RETRY ---

    private async Task<Mat?> LoadMatWithRetryAsync(string path, int index)
    {
        return await ExecuteWithRetryAsync(async () =>
        {
            // 1. Caricamento Atomico
            var header = await _ioService.ReadHeaderAsync(path);
            var pixels = await _ioService.ReadPixelDataAsync(path);
            
            if (header == null || pixels == null) return null; // Trigger Retry

            // 2. Estrazione Scaling
            double bScale = header.GetValue<double>("BSCALE") ?? 1.0;
            double bZero = header.GetValue<double>("BZERO") ?? 0.0;

            // 3. Conversione
            return _converter.RawToMat(pixels, bScale, bZero);
        }, 
        null, // Fallback value
        index);
    }

    private async Task<Point2D> CalculateShiftWithRetryAsync(string pathPrev, string pathCurr, int index)
    {
        return await ExecuteWithRetryAsync(async () =>
        {
            // Carichiamo entrambe le immagini (Header + Pixels)
            // Se uno qualsiasi fallisce, ritorniamo null per triggerare il retry globale
            
            var h1 = await _ioService.ReadHeaderAsync(pathPrev);
            var p1 = await _ioService.ReadPixelDataAsync(pathPrev);
            if (h1 == null || p1 == null) return null;

            var h2 = await _ioService.ReadHeaderAsync(pathCurr);
            var p2 = await _ioService.ReadPixelDataAsync(pathCurr);
            if (h2 == null || p2 == null) return null;

            // Parametri scaling
            double bs1 = h1.GetValue<double>("BSCALE") ?? 1.0;
            double bz1 = h1.GetValue<double>("BZERO") ?? 0.0;
            double bs2 = h2.GetValue<double>("BSCALE") ?? 1.0;
            double bz2 = h2.GetValue<double>("BZERO") ?? 0.0;

            using var m1 = _converter.RawToMat(p1, bs1, bz1);
            using var m2 = _converter.RawToMat(p2, bs2, bz2);
            
            return (Point2D?)_analysis.ComputeStarFieldShift(m1, m2);
        }, 
        new Point2D(0, 0), // Fallback shift zero
        index) ?? new Point2D(0, 0);
    }
}