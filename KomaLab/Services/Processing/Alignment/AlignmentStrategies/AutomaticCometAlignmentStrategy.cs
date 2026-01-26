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
    private readonly IImageEffectsEngine _effects;

    public AutomaticCometAlignmentStrategy(
        IFitsDataManager dataManager,
        IFitsMetadataService metadataService,
        IFitsOpenCvConverter converter, 
        IImageAnalysisEngine analysis,
        IImageEffectsEngine effects) : base(dataManager, analysis)
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
        
        var tasks = fileList.Select(async (fileRef, i) =>
        {
            await semaphore.WaitAsync(token);
            try 
            {
                token.ThrowIfCancellationRequested();
                var guess = (guessList != null && i < guessList.Count) ? guessList[i] : null;

                results[i] = await ExecuteWithRetryAsync(
                    operation: async () => await FindObjectCoreAsync(fileRef, guess),
                    itemIndex: i
                ) ?? guess;

                progress?.Report(new AlignmentProgressReport {
                    CurrentIndex = i + 1, 
                    TotalCount = fileList.Count,
                    FileName = System.IO.Path.GetFileName(fileRef.FilePath),
                    FoundCenter = results[i], 
                    Message = "Rilevamento automatico della cometa..."
                });
            }
            finally { semaphore.Release(); }
        });

        await Task.WhenAll(tasks);
        return results;
    }

    private async Task<Point2D?> FindObjectCoreAsync(FitsFileReference fileRef, Point2D? guess)
    {
        var data = await DataManager.GetDataAsync(fileRef.FilePath);
        
        var header = fileRef.ModifiedHeader ?? data.Header;
        double bScale = _metadataService.GetDoubleValue(header, "BSCALE", 1.0);
        double bZero = _metadataService.GetDoubleValue(header, "BZERO", 0.0);
        
        using var rawMat = _converter.RawToMat(data.PixelData, bScale, bZero);

        // 1. Sanitizzazione e ritaglio automatico sui dati validi (rimuove il padding NaN)
        var (mat, offset) = SanitizeAndCrop(rawMat, guess);

        try
        {
            // 2. Pulizia morfologica per isolare la chioma cometaria dalle stelle di fondo
            _effects.ApplyMorphologicalCleanup(mat, mat, kernelSize: 3);

            // 3. Calcolo del centroide locale
            Point2D localResult;

            if (guess.HasValue) 
            {
                // Raffinamento locale attorno al punto suggerito (NASA/JPL o giro precedente)
                var localGuess = new Point2D(guess.Value.X - offset.X, guess.Value.Y - offset.Y);
                int smartRadius = EstimateSmartRadius(mat);
                
                var roiRect = new Rect(
                    (int)(localGuess.X - smartRadius), 
                    (int)(localGuess.Y - smartRadius), 
                    smartRadius * 2, 
                    smartRadius * 2)
                    .Intersect(new Rect(0, 0, mat.Width, mat.Height));

                if (roiRect.Width > 4 && roiRect.Height > 4) 
                {
                    using var crop = new Mat(mat, roiRect);
                    var localCenter = _analysis.FindCenterOfLocalRegion(crop);
                    localResult = new Point2D(localCenter.X + roiRect.X, localCenter.Y + roiRect.Y);
                }
                else
                {
                    localResult = localGuess;
                }
            }
            else
            {
                // Ricerca globale sull'intera area valida (Blind Search)
                localResult = _analysis.FindCenterOfLocalRegion(mat);
            }

            // 4. Riconversione in coordinate assolute dell'immagine originale
            return new Point2D(localResult.X + offset.X, localResult.Y + offset.Y);
        }
        finally
        {
            mat.Dispose();
        }
    }

    private int EstimateSmartRadius(Mat mat) 
    {
        // Calcola dinamicamente il raggio di ricerca in base alla concentrazione del segnale
        int baseRadius = Math.Min(mat.Width, mat.Height) / 16;
        
        Cv2.MeanStdDev(mat, out Scalar mean, out Scalar stddev);
        using var thresh = new Mat();
        
        // Isola il nucleo luminoso (soglia statistica 5-sigma)
        Cv2.Threshold(mat, thresh, mean.Val0 + (5 * stddev.Val0), 255, ThresholdTypes.Binary);
        
        double density = (double)Cv2.CountNonZero(thresh) / (mat.Width * mat.Height);
        return Math.Max(30, (int)(baseRadius * (density > 0.001 ? 0.4 : 1.0)));
    }
}