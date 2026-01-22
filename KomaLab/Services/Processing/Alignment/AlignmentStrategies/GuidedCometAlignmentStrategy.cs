using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KomaLab.Models.Fits;
using KomaLab.Models.Primitives;
using KomaLab.Models.Processing;
using KomaLab.Services.Fits;
using KomaLab.Services.Fits.Conversion;
using KomaLab.Services.Fits.Metadata;
using KomaLab.Services.Processing.Engines;
using OpenCvSharp;

namespace KomaLab.Services.Processing.Alignment.AlignmentStrategies;

/// <summary>
/// Strategia di allineamento guidato (Cometa).
/// Calcola una traiettoria (lineare o densa/JPL) e raffina la posizione localmente.
/// </summary>
public class GuidedCometAlignmentStrategy : AlignmentStrategyBase
{
    private readonly IFitsMetadataService _metadataService;
    private readonly IFitsOpenCvConverter _converter;
    private readonly IImageAnalysisEngine _analysis;

    public GuidedCometAlignmentStrategy(
        IFitsDataManager dataManager,
        IFitsMetadataService metadataService,
        IFitsOpenCvConverter converter, 
        IImageAnalysisEngine analysis) : base(dataManager)
    {
        _metadataService = metadataService;
        _converter = converter;
        _analysis = analysis;
    }

    public override async Task<Point2D?[]> CalculateAsync(
        IEnumerable<FitsFileReference> files, 
        IEnumerable<Point2D?> guesses, 
        int searchRadius, 
        IProgress<AlignmentProgressReport>? progress = null,
        CancellationToken token = default)
    {
        var fileList = files.ToList();
        var inputList = guesses.ToList();
        var results = new Point2D?[fileList.Count];

        // 1. ANALISI INPUT
        int validPoints = inputList.Count(x => x.HasValue);
        bool isDenseTrajectory = validPoints > 2; 

        int firstIndex = inputList.FindIndex(g => g.HasValue);
        if (firstIndex == -1) return results;

        // 2. RAFFINAMENTO INIZIALE (Punto di ancoraggio)
        Point2D startEstimate = inputList[firstIndex]!.Value;
        
        // Raffiniamo il primo punto: è fondamentale per calcolare i delta successivi
        Point2D startRefined = await RefineCenterAsync(fileList[firstIndex], startEstimate, searchRadius) 
                               ?? startEstimate;
        
        results[firstIndex] = startRefined;

        // 3. ESECUZIONE (Loop Parallelo con Semaforo)
        using var semaphore = new SemaphoreSlim(GetOptimalConcurrency(fileList[0]));
        
        int lastIndex = inputList.FindLastIndex(g => g.HasValue);
        Point2D endEstimate = (lastIndex != -1) ? inputList[lastIndex]!.Value : startEstimate;

        var tasks = fileList.Select(async (fileRef, i) =>
        {
            if (i == firstIndex) return; 

            await semaphore.WaitAsync(token);
            try
            {
                token.ThrowIfCancellationRequested();

                Point2D estimatedPos;

                // --- RAMO A: STRATEGIA JPL / DENSE (Basata su Delta) ---
                if (isDenseTrajectory)
                {
                    if (inputList[i].HasValue)
                    {
                        // Spostamento relativo fornito dall'input (JPL) rispetto allo start originale
                        double deltaX = inputList[i]!.Value.X - startEstimate.X;
                        double deltaY = inputList[i]!.Value.Y - startEstimate.Y;

                        // Applichiamo il delta allo start reale raffinato
                        estimatedPos = new Point2D(startRefined.X + deltaX, startRefined.Y + deltaY);
                    }
                    else
                    {
                        results[i] = null;
                        return;
                    }
                }
                // --- RAMO B: STRATEGIA LINEARE (Interpolazione) ---
                else
                {
                    if (lastIndex == firstIndex) 
                    {
                        estimatedPos = startRefined;
                    }
                    else
                    {
                        double t = (double)(i - firstIndex) / (lastIndex - firstIndex);
                        double estX = startRefined.X + (endEstimate.X - startEstimate.X) * t;
                        double estY = startRefined.Y + (endEstimate.Y - startEstimate.Y) * t;
                        estimatedPos = new Point2D(estX, estY);
                    }
                }

                // 4. RAFFINAMENTO LOCALE
                // Eseguiamo l'operazione con retry e pulizia automatica della cache (tramite classe base)
                results[i] = await ExecuteWithRetryAsync(
                    async () => await RefineCenterAsync(fileRef, estimatedPos, searchRadius),
                    i
                ) ?? estimatedPos;

                progress?.Report(new AlignmentProgressReport { 
                    CurrentIndex = i + 1, 
                    TotalCount = fileList.Count, 
                    FileName = System.IO.Path.GetFileName(fileRef.FilePath),
                    FoundCenter = results[i], 
                    Message = isDenseTrajectory ? "Tracking JPL..." : "Interpolazione lineare..." 
                });
            }
            finally { semaphore.Release(); }
        });

        await Task.WhenAll(tasks);
        return results;
    }

    // =======================================================================
    // MOTORE DI RAFFINAMENTO (Ottimizzato)
    // =======================================================================
    private async Task<Point2D?> RefineCenterAsync(FitsFileReference fileRef, Point2D guess, int radius)
    {
        // 1. Caricamento dati
        var data = await DataManager.GetDataAsync(fileRef.FilePath);
        
        // 2. Priorità Header: usiamo il Modified se presente (per BSCALE/BZERO/CROP corretti)
        var header = fileRef.ModifiedHeader ?? data.Header;
        double bScale = _metadataService.GetDoubleValue(header, "BSCALE", 1.0);
        double bZero = _metadataService.GetDoubleValue(header, "BZERO", 0.0);
        
        // 3. Conversione Smart: targetBitDepth = null delega la scelta al convertitore (fedeltà scientifica)
        using var fullMat = _converter.RawToMat(data.PixelData, bScale, bZero);
        
        // 4. Analisi ROI
        int r = radius;
        int x = (int)guess.X;
        int y = (int)guess.Y;
        
        var roiRect = new Rect(x - r, y - r, r * 2, r * 2)
            .Intersect(new Rect(0, 0, fullMat.Width, fullMat.Height));

        if (roiRect.Width <= 4 || roiRect.Height <= 4) return guess;

        using var roiMat = new Mat(fullMat, roiRect);
        
        // Analisi locale per trovare il vero centroide della chioma
        var localCenter = _analysis.FindCenterOfLocalRegion(roiMat);
        
        return new Point2D(localCenter.X + roiRect.X, localCenter.Y + roiRect.Y);
    }
}