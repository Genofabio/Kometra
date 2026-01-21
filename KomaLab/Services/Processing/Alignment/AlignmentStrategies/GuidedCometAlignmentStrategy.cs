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

public class GuidedCometAlignmentStrategy : AlignmentStrategyBase
{
    private readonly IFitsMetadataService _metadataService;
    private readonly IFitsOpenCvConverter _converter;
    private readonly IImageAnalysisEngine _analysis;
    // GeometricEngine non serve più perché facciamo cropping locale per analisi, 
    // non warping di template.

    public GuidedCometAlignmentStrategy(
        IFitsDataManager dataManager,
        IFitsMetadataService metadataService,
        IFitsOpenCvConverter converter, 
        IImageAnalysisEngine analysis,
        IGeometricEngine geometricEngine) : base(dataManager)
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

        // 1. ANALISI INPUT: Capiamo se usare Interpolazione Lineare o Delta JPL
        // Se abbiamo punti validi su quasi tutti i frame, assumiamo che siano dati JPL (Dense)
        // Se ne abbiamo pochi (es. solo inizio e fine), assumiamo modalità Lineare.
        int validPoints = inputList.Count(x => x.HasValue);
        bool isDenseTrajectory = validPoints > 2; 

        // Troviamo il frame di partenza (il primo che ha un dato)
        int firstIndex = inputList.FindIndex(g => g.HasValue);
        if (firstIndex == -1) return results;

        // 2. RAFFINAMENTO INIZIALE (CRUCIALE)
        // Indipendentemente dalla strategia, dobbiamo trovare dov'è DAVVERO la cometa nel primo frame.
        // L'utente (o il JPL) ha dato una stima approssimativa -> Noi troviamo il centroide.
        Point2D startEstimate = inputList[firstIndex]!.Value;
        
        Point2D startRefined = await RefineCenterAsync(fileList[firstIndex].FilePath, startEstimate, searchRadius) 
                               ?? startEstimate;
        
        results[firstIndex] = startRefined;

        // 3. ESECUZIONE (Loop Parallelo)
        using var semaphore = new SemaphoreSlim(GetOptimalConcurrency(fileList[0]));
        
        // Determiniamo l'indice finale per l'interpolazione lineare
        int lastIndex = inputList.FindLastIndex(g => g.HasValue);
        Point2D endEstimate = (lastIndex != -1) ? inputList[lastIndex]!.Value : startEstimate;

        var tasks = fileList.Select(async (file, i) =>
        {
            if (i == firstIndex) return; // Il primo frame è già fatto e raffinato

            await semaphore.WaitAsync(token);
            try
            {
                token.ThrowIfCancellationRequested();

                Point2D estimatedPos;

                // --- RAMO A: STRATEGIA JPL / DENSE ---
                if (isDenseTrajectory)
                {
                    // Logica: "Quanto si è spostato il JPL rispetto al primo frame?"
                    // Calcoliamo il vettore di movimento relativo secondo i dati in input
                    if (inputList[i].HasValue)
                    {
                        double deltaX = inputList[i]!.Value.X - startEstimate.X;
                        double deltaY = inputList[i]!.Value.Y - startEstimate.Y;

                        // Applichiamo questo movimento alla posizione INIZIALE REALE (Raffinata)
                        estimatedPos = new Point2D(startRefined.X + deltaX, startRefined.Y + deltaY);
                    }
                    else
                    {
                        // Buco nei dati JPL? Saltiamo o interpoliamo (qui saltiamo per sicurezza)
                        results[i] = null;
                        return;
                    }
                }
                // --- RAMO B: STRATEGIA LINEARE ---
                else
                {
                    // Interpolazione pura tra Start e End
                    if (lastIndex == firstIndex) 
                    {
                        estimatedPos = startRefined; // Immagine ferma
                    }
                    else
                    {
                        double t = (double)(i - firstIndex) / (lastIndex - firstIndex);
                        double estX = startRefined.X + (endEstimate.X - startEstimate.X) * t;
                        double estY = startRefined.Y + (endEstimate.Y - startEstimate.Y) * t;
                        estimatedPos = new Point2D(estX, estY);
                    }
                }

                // 4. RAFFINAMENTO LOCALE (Il cuore della richiesta)
                // Ora che abbiamo la stima (Linear o JPL-Shifted), cerchiamo il vero centro.
                results[i] = await ExecuteWithRetryAsync(
                    async () => await RefineCenterAsync(file.FilePath, estimatedPos, searchRadius),
                    i
                ) ?? estimatedPos; // Fallback alla stima se il raffinamento fallisce (es. cometa troppo debole)

                progress?.Report(new AlignmentProgressReport { 
                    CurrentIndex = i + 1, 
                    TotalCount = fileList.Count, 
                    FoundCenter = results[i], 
                    Message = isDenseTrajectory ? "Tracking JPL..." : "Interpolazione..." 
                });
            }
            finally { semaphore.Release(); }
        });

        await Task.WhenAll(tasks);
        
        // Se siamo in modalità lineare, assicuriamoci di raffinare anche l'ultimo frame esplicitamente
        // (il loop sopra lo copre, ma se lastIndex != firstIndex, verifichiamo che non sia stato saltato per logica strana)
        if (!isDenseTrajectory && lastIndex > firstIndex && results[lastIndex] == null)
        {
             results[lastIndex] = await RefineCenterAsync(fileList[lastIndex].FilePath, endEstimate, searchRadius);
        }

        return results;
    }

    // =======================================================================
    // MOTORE DI RAFFINAMENTO (Identico alla modalità Manuale)
    // =======================================================================
    private async Task<Point2D?> RefineCenterAsync(string path, Point2D guess, int radius)
    {
        var data = await DataManager.GetDataAsync(path);
        double bScale = _metadataService.GetDoubleValue(data.Header, "BSCALE", 1.0);
        
        using var fullMat = _converter.RawToMat(data.PixelData, bScale);
        
        // Creazione ROI sicura
        int r = radius;
        int x = (int)guess.X;
        int y = (int)guess.Y;
        
        var roiRect = new Rect(x - r, y - r, r * 2, r * 2)
            .Intersect(new Rect(0, 0, fullMat.Width, fullMat.Height));

        // Se la ROI è troppo piccola o fuori bordo, ritorniamo la stima originale
        if (roiRect.Width <= 4 || roiRect.Height <= 4) return guess;

        using var roiMat = new Mat(fullMat, roiRect);
        
        // Usiamo l'algoritmo robusto (FindCenterOfLocalRegion) che gestisce rumore e blob
        var localCenter = _analysis.FindCenterOfLocalRegion(roiMat);
        
        // Trasformiamo coordinate locali (ROI) in globali
        return new Point2D(localCenter.X + roiRect.X, localCenter.Y + roiRect.Y);
    }
}