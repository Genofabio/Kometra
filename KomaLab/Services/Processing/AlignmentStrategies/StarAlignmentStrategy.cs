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
// AGGIORNAMENTO: Refactoring con AlignmentStrategyBase per resilienza I/O.
// ---------------------------------------------------------------------------

public class StarAlignmentStrategy : AlignmentStrategyBase
{
    private readonly IFitsIoService _ioService;
    private readonly IFitsImageDataConverter _converter;
    private readonly IImageAnalysisService _analysis;

    public StarAlignmentStrategy(
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
                // Qui usiamo ExecuteWithRetryAsync per caricare i due frame necessari al confronto.
                // È costoso caricare due file, ma succede solo se manca il WCS.
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
        // Usiamo il retry anche per leggere l'header, per massima sicurezza
        Point2D center0 = await ExecuteWithRetryAsync(async () => 
        {
            var header = await _ioService.ReadHeaderOnlyAsync(sourcePaths[0]);
            if (header == null) return null;
            return (Point2D?)new Point2D(header.GetIntValue("NAXIS1") / 2.0, header.GetIntValue("NAXIS2") / 2.0);
        }, new Point2D(0,0), 0) ?? new Point2D(0,0);

        results[0] = center0;
        progress?.Report((0, center0));

        // 2. Caricamento Iniziale Matrice 0
        Mat? prevMat = await LoadMatWithRetryAsync(sourcePaths[0], 0);
        
        if (prevMat == null) return; // Se fallisce il frame 0 anche dopo i retry, abortiamo.

        try
        {
            // Loop Sequenziale: Frame N vs Frame N-1
            for (int i = 1; i < sourcePaths.Count; i++)
            {
                // Carica il frame corrente con Retry
                Mat? currentMat = await LoadMatWithRetryAsync(sourcePaths[i], i);

                if (currentMat != null)
                {
                    // Calcolo Shift (CPU Intensive)
                    
                    Point2D shift = await Task.Run(() => _analysis.ComputeStarFieldShift(prevMat, currentMat));

                    Point2D prevCenter = results[i - 1]!.Value;
                    results[i] = new Point2D(prevCenter.X + shift.X, prevCenter.Y + shift.Y);

                    // --- SWAP EFFICIENTE ---
                    prevMat.Dispose();      // Buttiamo via la vecchia (i-1)
                    prevMat = currentMat;   // La corrente diventa la reference per il prossimo giro
                    currentMat = null;      // Annulliamo il ref locale per evitare il Dispose nel blocco finally implicito
                }
                else
                {
                    // Fallimento caricamento (dopo N retry): Manteniamo posizione stabile
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
            var data = await _ioService.LoadAsync(path);
            return data != null ? _converter.RawToMat(data) : null;
        }, 
        null, // Fallback value
        index);
    }

    private async Task<Point2D> CalculateShiftWithRetryAsync(string pathPrev, string pathCurr, int index)
    {
        // Questo metodo è un po' più complesso perché deve caricare due file.
        // Se uno fallisce, l'operazione fallisce.
        return await ExecuteWithRetryAsync(async () =>
        {
            var d1 = await _ioService.LoadAsync(pathPrev);
            var d2 = await _ioService.LoadAsync(pathCurr);

            if (d1 == null || d2 == null) return null;

            using var m1 = _converter.RawToMat(d1);
            using var m2 = _converter.RawToMat(d2);
            
            return (Point2D?)_analysis.ComputeStarFieldShift(m1, m2);
        }, 
        new Point2D(0, 0), // Fallback shift zero
        index) ?? new Point2D(0, 0);
    }
}