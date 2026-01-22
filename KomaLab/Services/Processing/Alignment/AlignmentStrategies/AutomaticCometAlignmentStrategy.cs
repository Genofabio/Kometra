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
        IImageEffectsEngine effects) : base(dataManager)
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

        // Utilizzo del semaforo per gestire il parallelismo in base alla RAM disponibile
        using var semaphore = new SemaphoreSlim(GetOptimalConcurrency(fileList[0]));
        
        var tasks = fileList.Select(async (fileRef, i) =>
        {
            await semaphore.WaitAsync(token);
            try 
            {
                token.ThrowIfCancellationRequested();
                
                var guess = (guessList != null && i < guessList.Count) ? guessList[i] : null;

                // Passiamo l'intero fileRef per gestire i metadati di sessione
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
        
        // 1. PRIORITÀ METADATI: Usiamo l'header modificato se presente
        var header = fileRef.ModifiedHeader ?? data.Header;
        double bScale = _metadataService.GetDoubleValue(header, "BSCALE", 1.0);
        double bZero = _metadataService.GetDoubleValue(header, "BZERO", 0.0);
        
        // 2. CARICAMENTO INTELLIGENTE: targetBitDepth = null delega al convertitore 
        // la scelta tra 32 e 64 bit per preservare la precisione originale.
        using var mat = _converter.RawToMat(data.PixelData, bScale, bZero);

        // ====================================================================
        // PRE-PROCESSING (Analisi sulla matrice scientifica)
        // ====================================================================
        
        // Utilizziamo direttamente 'mat' per l'analisi. 
        // Se il file era 16-bit, 'mat' è 32F. Se era 64-bit, 'mat' è 64F.
        // In entrambi i casi, gli engine di effetto lavorano correttamente.

        // 1. PULIZIA MORFOLOGICA (In-place)
        // Rimuove stelle puntiformi per isolare la chioma diffusa della cometa.
        _effects.ApplyMorphologicalCleanup(mat, mat, kernelSize: 3);

        // 2. PESO CENTRALE (VIGNETTATURA SINTETICA)
        // Se non abbiamo un punto di partenza, aiutiamo l'algoritmo a cercare verso il centro.
        if (!guess.HasValue)
        {
            _effects.ApplyCentralWeighting(mat, mat, sigmaScale: 0.7);
        }

        // ====================================================================
        // RICERCA E CENTRATURA
        // ====================================================================

        if (guess.HasValue) 
        {
            // Stima dinamica del raggio di ricerca in base alla densità del segnale
            int smartRadius = EstimateSmartRadius(mat);
            
            var roiRect = new Rect(
                (int)(guess.Value.X - smartRadius), 
                (int)(guess.Value.Y - smartRadius), 
                smartRadius * 2, 
                smartRadius * 2)
                .Intersect(new Rect(0, 0, mat.Width, mat.Height));

            if (roiRect.Width > 4 && roiRect.Height > 4) 
            {
                using var crop = new Mat(mat, roiRect);
                var local = _analysis.FindCenterOfLocalRegion(crop);
                return new Point2D(local.X + roiRect.X, local.Y + roiRect.Y);
            }
        }

        // Ricerca globale sull'intera inquadratura trattata
        return _analysis.FindCenterOfLocalRegion(mat);
    }

    private int EstimateSmartRadius(Mat mat) 
    {
        // Logica euristica per determinare quanto "allargare" la vista
        int baseRadius = Math.Min(mat.Width, mat.Height) / 16;
        
        Cv2.MeanStdDev(mat, out Scalar mean, out Scalar stddev);
        using var thresh = new Mat();
        
        // Soglia statistica a $5\sigma$ per isolare il nucleo
        Cv2.Threshold(mat, thresh, mean.Val0 + (5 * stddev.Val0), 255, ThresholdTypes.Binary);
        
        double density = (double)Cv2.CountNonZero(thresh) / (mat.Width * mat.Height);
        return Math.Max(30, (int)(baseRadius * (density > 0.001 ? 0.4 : 1.0)));
    }
}