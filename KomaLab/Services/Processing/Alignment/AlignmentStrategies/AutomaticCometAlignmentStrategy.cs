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

public class AutomaticCometAlignmentStrategy : AlignmentStrategyBase
{
    private readonly IFitsMetadataService _metadataService;
    private readonly IFitsOpenCvConverter _converter;
    private readonly IImageAnalysisEngine _analysis;
    private readonly IImageEffectsEngine _effects; // <--- NUOVA DIPENDENZA

    public AutomaticCometAlignmentStrategy(
        IFitsDataManager dataManager,
        IFitsMetadataService metadataService,
        IFitsOpenCvConverter converter, 
        IImageAnalysisEngine analysis,
        IImageEffectsEngine effects) : base(dataManager) // <--- INIEZIONE NEL COSTRUTTORE
    {
        _metadataService = metadataService;
        _converter = converter;
        _analysis = analysis;
        _effects = effects;
    }

    public override async Task<Point2D?[]> CalculateAsync(
        IEnumerable<FitsFileReference> files, 
        IEnumerable<Point2D?> guesses, 
        int searchRadius, 
        IProgress<AlignmentProgressReport>? progress = null,
        CancellationToken token = default)
    {
        var fileList = files.ToList();
        var guessList = guesses?.ToList(); 
        var results = new Point2D?[fileList.Count];

        using var semaphore = new SemaphoreSlim(GetOptimalConcurrency(fileList[0]));
        var tasks = fileList.Select(async (file, i) =>
        {
            await semaphore.WaitAsync(token);
            try {
                token.ThrowIfCancellationRequested();
                
                var guess = (guessList != null && i < guessList.Count) ? guessList[i] : null;

                results[i] = await ExecuteWithRetryAsync(
                    operation: async () => await FindObjectCoreAsync(file.FilePath, guess),
                    itemIndex: i
                ) ?? guess;

                progress?.Report(new AlignmentProgressReport {
                    CurrentIndex = i + 1, 
                    TotalCount = fileList.Count,
                    FileName = System.IO.Path.GetFileName(file.FilePath),
                    FoundCenter = results[i], 
                    Message = "Rilevamento automatico..."
                });
            }
            finally { semaphore.Release(); }
        });

        await Task.WhenAll(tasks);
        return results;
    }

    private async Task<Point2D?> FindObjectCoreAsync(string path, Point2D? guess)
    {
        var data = await DataManager.GetDataAsync(path);
        double bScale = _metadataService.GetDoubleValue(data.Header, "BSCALE", 1.0);
        double bZero = _metadataService.GetDoubleValue(data.Header, "BZERO", 0.0);
        
        using var mat = _converter.RawToMat(data.PixelData, bScale, bZero);

        // ====================================================================
        // PRE-PROCESSING (PIPELINE DI EFFETTI)
        // ====================================================================
        
        // Creiamo una copia di lavoro in alta precisione (Double) per l'analisi
        using var analysisMat = new Mat();
        mat.ConvertTo(analysisMat, MatType.CV_64FC1);

        // 1. PULIZIA MORFOLOGICA
        // Rimuove stelle puntiformi e hot pixel, preservando la natura diffusa della cometa.
        // Utile sia se abbiamo un guess (pulisce la ROI) sia se cerchiamo alla cieca.
        _effects.ApplyMorphologicalCleanup(analysisMat, analysisMat, kernelSize: 3);

        // 2. PESO CENTRALE (VIGNETTATURA SINTETICA)
        // Applicato SOLO se non abbiamo un guess manuale (ricerca cieca).
        // Aiuta a ignorare artefatti ai bordi o stelle luminose periferiche,
        // assumendo che il target sia grossomodo al centro dell'inquadratura.
        if (!guess.HasValue)
        {
            _effects.ApplyCentralWeighting(analysisMat, analysisMat, sigmaScale: 0.7);
        }

        // ====================================================================
        // RICERCA (ANALISI)
        // ====================================================================

        if (guess.HasValue) {
            // Nota: Stimiamo il raggio sulla matrice originale 'mat' per avere 
            // la statistica reale del rumore/densità, non quella filtrata.
            int smartRadius = EstimateSmartRadius(mat);
            
            var roiRect = new Rect((int)(guess.Value.X - smartRadius), (int)(guess.Value.Y - smartRadius), smartRadius * 2, smartRadius * 2)
                .Intersect(new Rect(0, 0, mat.Width, mat.Height));

            if (roiRect.Width > 4 && roiRect.Height > 4) {
                // Eseguiamo il fit sulla matrice PULITA ('analysisMat')
                using var crop = new Mat(analysisMat, roiRect);
                var local = _analysis.FindCenterOfLocalRegion(crop);
                return new Point2D(local.X + roiRect.X, local.Y + roiRect.Y);
            }
        }

        // Ricerca globale sulla matrice trattata
        return _analysis.FindCenterOfLocalRegion(analysisMat);
    }

    private int EstimateSmartRadius(Mat mat) {
        int baseRadius = Math.Min(mat.Width, mat.Height) / 16;
        Cv2.MeanStdDev(mat, out Scalar mean, out Scalar stddev);
        using var thresh = new Mat();
        Cv2.Threshold(mat, thresh, mean.Val0 + (5 * stddev.Val0), 255, ThresholdTypes.Binary);
        double density = (double)Cv2.CountNonZero(thresh) / (mat.Width * mat.Height);
        return Math.Max(30, (int)(baseRadius * (density > 0.001 ? 0.4 : 1.0)));
    }
}