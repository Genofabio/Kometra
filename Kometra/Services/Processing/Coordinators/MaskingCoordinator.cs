using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Kometra.Models.Fits;
using Kometra.Models.Fits.Structure;
using Kometra.Models.Processing;
using Kometra.Models.Processing.Batch;
using Kometra.Models.Processing.Masking;
using Kometra.Services.Fits;
using Kometra.Services.Fits.Conversion;
using Kometra.Services.Fits.Metadata;
using Kometra.Services.Processing.Batch;
using Kometra.Services.Processing.Engines;
using OpenCvSharp;

namespace Kometra.Services.Processing.Coordinators;

public class MaskingCoordinator : IMaskingCoordinator
{
    private readonly IBatchProcessingService _batchService;
    private readonly IFitsDataManager _dataManager;
    private readonly IFitsOpenCvConverter _converter;
    private readonly IFitsMetadataService _metadataService;
    private readonly IRadiometryEngine _radiometryEngine;
    private readonly ISegmentationEngine _segmentationEngine;
    private readonly IInpaintingEngine _inpaintingEngine;

    public MaskingCoordinator(
        IBatchProcessingService batchService,
        IFitsDataManager dataManager,
        IFitsOpenCvConverter converter,
        IFitsMetadataService metadataService,
        IRadiometryEngine radiometryEngine,
        ISegmentationEngine segmentationEngine,
        IInpaintingEngine inpaintingEngine)
    {
        _batchService = batchService ?? throw new ArgumentNullException(nameof(batchService));
        _dataManager = dataManager ?? throw new ArgumentNullException(nameof(dataManager));
        _converter = converter ?? throw new ArgumentNullException(nameof(converter));
        _metadataService = metadataService ?? throw new ArgumentNullException(nameof(metadataService));
        _radiometryEngine = radiometryEngine ?? throw new ArgumentNullException(nameof(radiometryEngine));
        _segmentationEngine = segmentationEngine ?? throw new ArgumentNullException(nameof(segmentationEngine));
        _inpaintingEngine = inpaintingEngine ?? throw new ArgumentNullException(nameof(inpaintingEngine));
    }

    public async Task<Array> ProcessPreviewAsync(
        FitsFileReference sourceFile, 
        MaskingParameters p, 
        CancellationToken token = default)
    {
        var data = await _dataManager.GetDataAsync(sourceFile.FilePath);
        var imageHdu = data.FirstImageHdu ?? data.PrimaryHdu;
        if (imageHdu == null) throw new InvalidOperationException("Nessuna immagine valida per l'anteprima.");

        using Mat src = _converter.RawToMat(imageHdu.PixelData, 1.0, 0.0, null);
        
        return await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();

            // 1. Statistiche (Restituisce tupla: Mean, StdDev, MedianApprox)
            var stats = _radiometryEngine.ComputeRobustStatistics(src, sigma: 3.0, maxIterations: 5);
            token.ThrowIfCancellationRequested();

            // 2. Calcolo Maschere & Inpainting (Incapsulato per gestione memoria)
            using Mat starlessImage = GenerateStarlessImage(src, stats, p);

            token.ThrowIfCancellationRequested();

            // 3. Conversione Finale
            var depth = src.Type() == MatType.CV_32FC1 ? FitsBitDepth.Float : FitsBitDepth.Double;
            return _converter.MatToRaw(starlessImage, depth);

        }, token);
    }

    public async Task<List<string>> ExecuteBatchAsync(
        IEnumerable<FitsFileReference> sourceFiles,
        MaskingParameters p,
        IProgress<BatchProgressReport>? progress = null,
        CancellationToken token = default)
    {
        Func<Mat, Mat, FitsHeader, int, Task> removalOp = async (src, dst, header, index) =>
        {
            await Task.Run(() =>
            {
                var stats = _radiometryEngine.ComputeRobustStatistics(src, sigma: 3.0, maxIterations: 5);

                // Generazione immagine senza stelle
                using Mat starlessResult = GenerateStarlessImage(src, stats, p);

                // Copia nel buffer di destinazione
                starlessResult.CopyTo(dst);

                // Aggiornamento Header
                _metadataService.AddValue(header, "HISTORY", "Kometra: Stars Removed");
                _metadataService.AddValue(header, "HISTORY", $"Params: Thr={p.StarThresholdSigma}, Dil={p.StarDilation}, MinDiam={p.MinStarDiameter}");
            }, token);
        };

        return await _batchService.ProcessFilesAsync(
            sourceFiles,
            "Starless", 
            removalOp,
            progress,
            token);
    }
    
    public async Task<(Array StarMask, Array CometMask)> CalculateMasksPreviewAsync(
        FitsFileReference sourceFile, 
        MaskingParameters p, 
        CancellationToken token = default)
    {
        var data = await _dataManager.GetDataAsync(sourceFile.FilePath);
        var imageHdu = data.FirstImageHdu ?? data.PrimaryHdu;
        if (imageHdu == null) return (new byte[0,0], new byte[0,0]);

        using Mat src = _converter.RawToMat(imageHdu.PixelData, 1.0, 0.0, null);
        
        return await Task.Run(() =>
        {
            var stats = _radiometryEngine.ComputeRobustStatistics(src, 3.0, 5);
            
            // Qui usiamo stats.MedianApprox e stats.StdDev direttamente
            using Mat cometMask = _segmentationEngine.ComputeCometMask(src, stats.MedianApprox, stats.StdDev, p);
            using Mat starMask = _segmentationEngine.ComputeStarMask(src, cometMask, stats.MedianApprox, stats.StdDev, p);

            return (
                _converter.MatToRaw(starMask, FitsBitDepth.UInt8),
                _converter.MatToRaw(cometMask, FitsBitDepth.UInt8)
            );
        }, token);
    }

    // --- HELPER PER GESTIONE SCOPE MEMORIA ---
    
    // [FIX] La firma ora accetta esattamente la tupla restituita da IRadiometryEngine
    private Mat GenerateStarlessImage(
        Mat src, 
        (double Mean, double StdDev, double MedianApprox) stats, 
        MaskingParameters p)
    {
        // Usiamo i campi della tupla (MedianApprox, StdDev)
        using Mat cometMask = _segmentationEngine.ComputeCometMask(src, stats.MedianApprox, stats.StdDev, p);
        using Mat starMask = _segmentationEngine.ComputeStarMask(src, cometMask, stats.MedianApprox, stats.StdDev, p);
        
        // InpaintStars usa le maschere per generare il risultato.
        // Alla fine di questo blocco, le maschere vengono distrutte liberando memoria.
        return _inpaintingEngine.InpaintStars(src, starMask, cometMask);
    }
}