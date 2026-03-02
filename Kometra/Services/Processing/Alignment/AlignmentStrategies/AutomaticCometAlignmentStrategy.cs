using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kometra.Models.Fits;
using Kometra.Models.Primitives;
using Kometra.Models.Processing;
using Kometra.Models.Processing.Alignment;
using Kometra.Services.Fits;
using Kometra.Services.Fits.Conversion;
using Kometra.Services.Fits.Metadata;
using Kometra.Services.Processing.Engines;
using OpenCvSharp;

namespace Kometra.Services.Processing.Alignment.AlignmentStrategies;

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
        // Lettura asincrona da disco (I/O veloce, non blocca la UI)
        var data = await DataManager.GetDataAsync(fileRef.FilePath);
        
        // [MODIFICA MEF] Accesso sicuro all'HDU immagine
        // FirstImageHdu restituisce la prima estensione valida (non vuota)
        var imageHdu = data.FirstImageHdu ?? data.PrimaryHdu;

        if (imageHdu == null) 
            throw new InvalidOperationException("Nessuna immagine valida trovata nel file FITS.");

        // Preferenza all'header modificato in RAM, altrimenti quello dell'HDU
        var header = fileRef.ModifiedHeader ?? imageHdu.Header;

        double bScale = _metadataService.GetDoubleValue(header, "BSCALE", 1.0);
        double bZero = _metadataService.GetDoubleValue(header, "BZERO", 0.0);
        
        // =========================================================================
        // FIX UI LAG: Avvolgiamo tutta l'elaborazione CPU-bound in un Task.Run
        // =========================================================================
        return await Task.Run(() =>
        {
            // Usiamo i PixelData dell'HDU immagine specifica
            using var rawMat = _converter.RawToMat(imageHdu.PixelData, bScale, bZero);

            // 1. Sanitizzazione e ritaglio automatico sui dati validi (rimuove il padding NaN)
            var (mat, offset) = SanitizeAndCrop(rawMat, guess);

            try
            {
                // 2. Pulizia morfologica per isolare la chioma cometaria dalle stelle di fondo
                _effects.ApplyMorphologicalCleanup(mat, mat, kernelSize: 3);

                // 3. Determinazione del punto di partenza (Guess locale o Blind Search)
                Point2D localInitialPoint;

                if (guess.HasValue) 
                {
                    // Abbiamo i dati WCS: convertiamo il punto suggerito in coordinate locali
                    localInitialPoint = new Point2D(guess.Value.X - offset.X, guess.Value.Y - offset.Y);
                }
                else
                {
                    // Niente WCS: usiamo il vecchio metodo per trovare approssimativamente l'oggetto nell'immagine intera
                    localInitialPoint = _analysis.FindCenterOfLocalRegion(mat);
                }

                // 4. Costruzione della ROI e Raffinamento con il nuovo modello
                Point2D localResult;
                int smartRadius = EstimateSmartRadius(mat);
                
                var roiRect = new Rect(
                    (int)(localInitialPoint.X - smartRadius), 
                    (int)(localInitialPoint.Y - smartRadius), 
                    smartRadius * 2, 
                    smartRadius * 2)
                    .Intersect(new Rect(0, 0, mat.Width, mat.Height));

                // Procediamo al fit solo se la ROI è grande a sufficienza
                if (roiRect.Width > 4 && roiRect.Height > 4) 
                {
                    using var crop = new Mat(mat, roiRect);
                    
                    // Utilizziamo il nuovo metodo per trovare il centro asimmetrico
                    var localRefinedCenter = _analysis.FindAsymmetricQuadrantCenter(crop);
                    
                    // Fallback di sicurezza: se il fit fallisce restituiamo il punto di partenza calcolato al punto 3
                    if (double.IsInfinity(localRefinedCenter.X) || double.IsInfinity(localRefinedCenter.Y) || 
                        localRefinedCenter.X < 0 || localRefinedCenter.Y < 0)
                    {
                        System.Diagnostics.Debug.WriteLine($"[WARNING] Fit asimmetrico fallito su {System.IO.Path.GetFileName(fileRef.FilePath)}. Ripiego sul punto di stima iniziale.");
                        localResult = localInitialPoint;
                    }
                    else
                    {
                        // Riconversione delle coordinate del crop in coordinate dell'immagine 'mat'
                        localResult = new Point2D(localRefinedCenter.X + roiRect.X, localRefinedCenter.Y + roiRect.Y);
                    }
                }
                else
                {
                    localResult = localInitialPoint;
                }

                // 5. Riconversione in coordinate assolute dell'immagine FITS originale
                return new Point2D(localResult.X + offset.X, localResult.Y + offset.Y);
            }
            finally
            {
                mat.Dispose();
            }
        });
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