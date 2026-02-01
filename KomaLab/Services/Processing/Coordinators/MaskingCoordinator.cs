using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KomaLab.Models.Fits;
using KomaLab.Models.Fits.Structure;
using KomaLab.Models.Processing;
using KomaLab.Models.Processing.Masking;
using KomaLab.Services.Fits;
using KomaLab.Services.Fits.Conversion;
using KomaLab.Services.Fits.Metadata;
using KomaLab.Services.Processing.Batch;
using KomaLab.Services.Processing.Engines;
using OpenCvSharp;

namespace KomaLab.Services.Processing.Coordinators;

public class MaskingCoordinator : IMaskingCoordinator
{
    private readonly IBatchProcessingService _batchService;
    private readonly IFitsDataManager _dataManager;
    private readonly IFitsOpenCvConverter _converter;
    private readonly IFitsMetadataService _metadataService;
    private readonly IRadiometryEngine _radiometryEngine;
    private readonly ISegmentationEngine _segmentationEngine;

    public MaskingCoordinator(
        IBatchProcessingService batchService,
        IFitsDataManager dataManager,
        IFitsOpenCvConverter converter,
        IFitsMetadataService metadataService,
        IRadiometryEngine radiometryEngine,
        ISegmentationEngine segmentationEngine)
    {
        _batchService = batchService ?? throw new ArgumentNullException(nameof(batchService));
        _dataManager = dataManager ?? throw new ArgumentNullException(nameof(dataManager));
        _converter = converter ?? throw new ArgumentNullException(nameof(converter));
        _metadataService = metadataService ?? throw new ArgumentNullException(nameof(metadataService));
        _radiometryEngine = radiometryEngine ?? throw new ArgumentNullException(nameof(radiometryEngine));
        _segmentationEngine = segmentationEngine ?? throw new ArgumentNullException(nameof(segmentationEngine));
    }

    public async Task<(Array StarMaskPixels, Array CometMaskPixels)> CalculatePreviewAsync(
        FitsFileReference sourceFile, 
        MaskingParameters p, 
        CancellationToken token = default)
    {
        var data = await _dataManager.GetDataAsync(sourceFile.FilePath);
        var imageHdu = data.FirstImageHdu ?? data.PrimaryHdu;
        if (imageHdu == null) throw new InvalidOperationException("Nessuna immagine valida per l'anteprima.");

        // [MODIFICA] targetDepth: null 
        // Lasciamo che il converter decida il formato ottimale (32F o 64F) in base all'input.
        using Mat src = _converter.RawToMat(imageHdu.PixelData, 1.0, 0.0, null);
        
        return await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();

            // 1. Statistiche (Funziona su 32F e 64F)
            var stats = _radiometryEngine.ComputeRobustStatistics(src, sigma: 3.0, maxIterations: 5);
            token.ThrowIfCancellationRequested();

            // 2. Calcola Maschere (Funziona su 32F e 64F)
            using Mat cometMask = _segmentationEngine.ComputeCometMask(src, stats.MedianApprox, stats.StdDev, p);
            token.ThrowIfCancellationRequested();

            using Mat starMask = _segmentationEngine.ComputeStarMask(src, cometMask, stats.MedianApprox, stats.StdDev, p);
            token.ThrowIfCancellationRequested();

            // 3. Output per UI
            return (
                _converter.MatToRaw(starMask, FitsBitDepth.UInt8),
                _converter.MatToRaw(cometMask, FitsBitDepth.UInt8)
            );
        }, token);
    }

    public async Task<List<string>> ExecuteBatchAsync(
        IEnumerable<FitsFileReference> sourceFiles,
        MaskingParameters p,
        IProgress<BatchProgressReport>? progress = null,
        CancellationToken token = default)
    {
        Func<Mat, Mat, FitsHeader, int, Task> maskingOp = async (src, dst, header, index) =>
        {
            await Task.Run(() =>
            {
                // [MODIFICA] Nessuna conversione forzata.
                // 'src' arriva dal BatchService che usa lo stesso Converter, 
                // quindi è già Float o Double a seconda del dato raw.
                
                // 1. Statistiche
                var stats = _radiometryEngine.ComputeRobustStatistics(src, sigma: 3.0, maxIterations: 5);

                // 2. Calcolo Maschere
                using Mat cometMask = _segmentationEngine.ComputeCometMask(src, stats.MedianApprox, stats.StdDev, p);
                using Mat starMask = _segmentationEngine.ComputeStarMask(src, cometMask, stats.MedianApprox, stats.StdDev, p);

                // 3. Scaling Output
                // Se salviamo su 16-bit, scaliamo 255 -> 65535.
                double scaleFactor = 1.0;
                if (dst.Depth() == MatType.CV_16U || dst.Depth() == MatType.CV_16S)
                {
                    scaleFactor = 65535.0 / 255.0;
                }
                else if (dst.Depth() == MatType.CV_32F || dst.Depth() == MatType.CV_64F)
                {
                    scaleFactor = 1.0 / 255.0; 
                }

                starMask.ConvertTo(dst, dst.Type(), alpha: scaleFactor);

                // 4. Metadata
                _metadataService.AddValue(header, "HISTORY", "$KomaLab - Star Masked. Params: Sigma={p.StarThresholdSigma}, Dilation={p.StarDilation}");
            }, token);
        };

        return await _batchService.ProcessFilesAsync(
            sourceFiles,
            "StarMask",
            maskingOp,
            progress,
            token);
    }
}