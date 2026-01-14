using System;
using System.Collections.Generic;
using System.IO;
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
// AGGIORNAMENTO: Refactoring con AlignmentStrategyBase per resilienza (Retry).
// ---------------------------------------------------------------------------

public class GuidedCometAlignmentStrategy : AlignmentStrategyBase
{
    private readonly IFitsIoService _ioService;
    private readonly IFitsImageDataConverter _converter;
    private readonly IImageOperationService _operations;

    public GuidedCometAlignmentStrategy(
        IFitsIoService ioService, 
        IFitsImageDataConverter converter, 
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

        // Validazione Input
        var p1Guess = guesses.FirstOrDefault();
        var pNGuess = guesses.LastOrDefault();

        if (n < 2 || !p1Guess.HasValue || !pNGuess.HasValue) 
        {
            return guesses.ToArray();
        }

        // 1. Concorrenza ottimizzata (Dalla classe base)
        int maxConcurrency = GetOptimalConcurrency(sourcePaths[0]);
        using var semaphore = new SemaphoreSlim(maxConcurrency);
        
        Mat? templateMat = null;

        try
        {
            // ---------------------------------------------------------
            // FASE 1: Inizializzazione Estremi (Protetta da Retry)
            // ---------------------------------------------------------

            // A. FRAME 0 (START) - Estrazione Template
            // Usiamo ExecuteWithRetryAsync per essere resilienti ai lock del file system
            var t0Result = await ExecuteWithRetryAsync(
                operation: async () => 
                {
                    var data = await _ioService.LoadAsync(sourcePaths[0]);
                    if (data == null) return null; // Triggera retry
                    // Nota: ExtractRefinedTemplate non è asincrono ma è pesante, ok nel Task
                    return (Result?)_operations.ExtractRefinedTemplate(data, p1Guess.Value, searchRadius);
                },
                fallbackValue: null,
                itemIndex: 0
            );

            if (t0Result == null) return results; // Fallimento critico su Frame 0

            templateMat = t0Result.Value.Template;
            Point2D centerStart = t0Result.Value.RefinedCenter;
            results[0] = centerStart;
            progress?.Report((0, centerStart));

            // B. FRAME N (END) - Calcolo Traiettoria
            Point2D centerEnd = pNGuess.Value;
            
            var tEndResult = await ExecuteWithRetryAsync(
                operation: async () =>
                {
                    var data = await _ioService.LoadAsync(sourcePaths[n - 1]);
                    if (data == null) return null;
                    
                    var t = _operations.ExtractRefinedTemplate(data, pNGuess.Value, searchRadius);
                    t.Template?.Dispose(); // Non ci serve il template finale, solo il punto
                    return (Point2D?)t.RefinedCenter;
                },
                fallbackValue: pNGuess.Value, // Fallback al guess se il file è illeggibile
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
            // FASE 3: Tracking Parallelo (Protetto da Retry)
            // ---------------------------------------------------------
            var tasks = new List<Task>();

            for (int i = 1; i < n - 1; i++)
            {
                int index = i;
                string path = sourcePaths[index];

                // Calcolo Posizione Attesa
                Point2D expectedPoint;
                if (index < guesses.Count && guesses[index].HasValue)
                {
                    expectedPoint = guesses[index]!.Value;
                }
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
                        // Eseguiamo la logica core con resilienza
                        Point2D? result = await ExecuteWithRetryAsync(
                            operation: async () => await ProcessFrameCoreAsync(path, templateMat, expectedPoint, searchRadius),
                            fallbackValue: expectedPoint, // Se fallisce I/O, usiamo la stima lineare
                            itemIndex: index
                        );
                        
                        results[index] = result;
                        progress?.Report((index, result));
                    }
                    finally
                    {
                        semaphore.Release();
                    }
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
    /// Logica Core per singolo frame: Carica -> Cerca Template.
    /// Ritorna null solo se c'è un errore di I/O (per scatenare il retry).
    /// Ritorna expectedPoint se l'I/O va bene ma il template non viene trovato.
    /// </summary>
    private async Task<Point2D?> ProcessFrameCoreAsync(string path, Mat templateMat, Point2D expectedPoint, int radius)
    {
        var fitsData = await _ioService.LoadAsync(path);
        if (fitsData == null) return null; // Trigger Retry (errore disco/lock)

        using Mat fullImage = _converter.RawToMat(fitsData);

        // Template Matching Locale
        Point2D? foundMatch = _operations.FindTemplatePosition(
            fullImage, 
            templateMat, 
            expectedPoint, 
            radius);
            
        // Se matching fallisce (nuvole/rumore), ritorniamo il punto stimato.
        // NON ritorniamo null, altrimenti il sistema riproverebbe inutilmente per un problema non di I/O.
        return foundMatch ?? expectedPoint;
    }

    // Helper struct per gestire il ritorno della tupla nel wrapper del retry
    private struct Result { public Mat Template; public Point2D RefinedCenter; 
        public static implicit operator Result((Mat t, Point2D p) tuple) => new Result { Template = tuple.t, RefinedCenter = tuple.p };
    }
}