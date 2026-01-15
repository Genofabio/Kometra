using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KomaLab.Models.Primitives;
using KomaLab.Services.Fits;
using OpenCvSharp;

namespace KomaLab.Services.Processing.AlignmentStrategies;

// ---------------------------------------------------------------------------
// FILE: GuidedCometAlignmentStrategy.cs
// RUOLO: Strategia Allineamento (Tracking)
// VERSIONE: Aggiornata per Architettura No-FitsImageData
// ---------------------------------------------------------------------------

public class GuidedCometAlignmentStrategy : AlignmentStrategyBase
{
    private readonly IFitsIoService _ioService;
    private readonly IFitsOpenCvConverter _converter;
    private readonly IImageOperationService _operations;

    public GuidedCometAlignmentStrategy(
        IFitsIoService ioService, 
        IFitsOpenCvConverter converter, 
        IImageOperationService operations)
    {
        _ioService = ioService ?? throw new ArgumentNullException(nameof(ioService));
        _converter = converter ?? throw new ArgumentNullException(nameof(converter));
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
    }

    public override async Task<Point2D?[]> CalculateAsync(
        List<string> sourcePaths, 
        List<Point2D?> guesses, 
        int searchRadius, 
        IProgress<(int Index, Point2D? Center)>? progress)
    {
        int n = sourcePaths.Count;
        var results = new Point2D?[n];

        var p1Guess = guesses.FirstOrDefault();
        var pNGuess = guesses.LastOrDefault();

        if (n < 2 || !p1Guess.HasValue || !pNGuess.HasValue) 
        {
            return guesses.ToArray();
        }

        int maxConcurrency = GetOptimalConcurrency(sourcePaths[0]);
        using var semaphore = new SemaphoreSlim(maxConcurrency);
        
        Mat? templateMat = null;

        try
        {
            // ---------------------------------------------------------
            // FASE 1: Inizializzazione Estremi (Protetta da Retry)
            // ---------------------------------------------------------

            // A. FRAME 0 (START) - Estrazione Template
            var t0Result = await ExecuteWithRetryAsync(
                operation: async () => 
                {
                    // Caricamento Separato
                    var header = await _ioService.ReadHeaderAsync(sourcePaths[0]);
                    var pixels = await _ioService.ReadPixelDataAsync(sourcePaths[0]);
                    
                    if (header == null || pixels == null) return null; // Trigger Retry

                    // Estrazione parametri scala
                    double bScale = header.GetValue<double>("BSCALE") ?? 1.0;
                    double bZero = header.GetValue<double>("BZERO") ?? 0.0;

                    // Conversione e Estrazione
                    using var mat = _converter.RawToMat(pixels, bScale, bZero);
                    
                    // La ExtractRefinedTemplate crea una NUOVA Mat (copia), 
                    // quindi possiamo disporre 'mat' (source) qui dentro tranquillamente.
                    return (Result?)_operations.ExtractRefinedTemplate(mat, p1Guess.Value, searchRadius);
                },
                fallbackValue: null,
                itemIndex: 0
            );

            if (t0Result == null) return results; // Fallimento critico

            templateMat = t0Result.Value.Template;
            Point2D centerStart = t0Result.Value.RefinedCenter;
            results[0] = centerStart;
            progress?.Report((0, centerStart));

            // B. FRAME N (END) - Calcolo Traiettoria
            Point2D centerEnd = pNGuess.Value;
            
            var tEndResult = await ExecuteWithRetryAsync(
                operation: async () =>
                {
                    var header = await _ioService.ReadHeaderAsync(sourcePaths[n - 1]);
                    var pixels = await _ioService.ReadPixelDataAsync(sourcePaths[n - 1]);
                    if (header == null || pixels == null) return null;
                    
                    double bScale = header.GetValue<double>("BSCALE") ?? 1.0;
                    double bZero = header.GetValue<double>("BZERO") ?? 0.0;

                    using var mat = _converter.RawToMat(pixels, bScale, bZero);

                    var t = _operations.ExtractRefinedTemplate(mat, pNGuess.Value, searchRadius);
                    t.Template?.Dispose(); // Qui ci serve solo il punto, buttiamo il template
                    
                    return (Point2D?)t.RefinedCenter;
                },
                fallbackValue: pNGuess.Value, 
                itemIndex: n - 1
            );

            if (tEndResult.HasValue) centerEnd = tEndResult.Value;
            
            results[n - 1] = centerEnd;
            progress?.Report((n - 1, centerEnd));

            // ---------------------------------------------------------
            // FASE 2: Calcolo Traiettoria (Lineare)
            // ---------------------------------------------------------
            double stepX = (centerEnd.X - centerStart.X) / (n - 1);
            double stepY = (centerEnd.Y - centerStart.Y) / (n - 1);

            // ---------------------------------------------------------
            // FASE 3: Tracking Parallelo
            // ---------------------------------------------------------
            var tasks = new List<Task>();

            for (int i = 1; i < n - 1; i++)
            {
                int index = i;
                string path = sourcePaths[index];

                Point2D expectedPoint;
                if (index < guesses.Count && guesses[index].HasValue)
                    expectedPoint = guesses[index]!.Value;
                else
                {
                    double linX = centerStart.X + (index * stepX);
                    double linY = centerStart.Y + (index * stepY);
                    expectedPoint = new Point2D(linX, linY);
                }

                tasks.Add(Task.Run(async () =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        Point2D? result = await ExecuteWithRetryAsync(
                            operation: async () => await ProcessFrameCoreAsync(path, templateMat, expectedPoint, searchRadius),
                            fallbackValue: expectedPoint, 
                            itemIndex: index
                        );
                        
                        results[index] = result;
                        progress?.Report((index, result));
                    }
                    finally { semaphore.Release(); }
                }));
            }

            await Task.WhenAll(tasks);
        }
        finally
        {
            templateMat?.Dispose();
        }

        return results;
    }

    /// <summary>
    /// Logica Core per singolo frame: Carica -> Converti -> Match Template.
    /// </summary>
    private async Task<Point2D?> ProcessFrameCoreAsync(string path, Mat templateMat, Point2D expectedPoint, int radius)
    {
        // ADATTAMENTO ARCHITETTURA: Caricamento separato
        var header = await _ioService.ReadHeaderAsync(path);
        var pixels = await _ioService.ReadPixelDataAsync(path);
        
        if (header == null || pixels == null) return null; // Trigger Retry

        double bScale = header.GetValue<double>("BSCALE") ?? 1.0;
        double bZero = header.GetValue<double>("BZERO") ?? 0.0;

        using Mat fullImage = _converter.RawToMat(pixels, bScale, bZero);

        // Template Matching Locale
        Point2D? foundMatch = _operations.FindTemplatePosition(
            fullImage, 
            templateMat, 
            expectedPoint, 
            radius);
            
        // Se non trova il template (es. nuvole), usa la stima lineare.
        // NON tornare null qui, altrimenti il retry loop proverebbe a ricaricare il file inutilmente.
        return foundMatch ?? expectedPoint;
    }

    // Helper struct
    private struct Result { 
        public Mat Template; 
        public Point2D RefinedCenter; 
        public static implicit operator Result((Mat t, Point2D p) tuple) => new Result { Template = tuple.t, RefinedCenter = tuple.p };
    }
}