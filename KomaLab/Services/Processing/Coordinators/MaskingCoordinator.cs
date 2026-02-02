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

    /// <summary>
    /// Calcola l'anteprima della rimozione stelle per una singola immagine.
    /// Restituisce i pixel dell'immagine processata (Starless).
    /// </summary>
    public async Task<Array> ProcessPreviewAsync(
        FitsFileReference sourceFile, 
        MaskingParameters p, 
        CancellationToken token = default)
    {
        var data = await _dataManager.GetDataAsync(sourceFile.FilePath);
        var imageHdu = data.FirstImageHdu ?? data.PrimaryHdu;
        if (imageHdu == null) throw new InvalidOperationException("Nessuna immagine valida per l'anteprima.");

        // Lasciamo che il converter decida il formato ottimale (32F o 64F) in base all'input.
        using Mat src = _converter.RawToMat(imageHdu.PixelData, 1.0, 0.0, null);
        
        return await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();

            // 1. Statistiche (Background & Noise)
            var stats = _radiometryEngine.ComputeRobustStatistics(src, sigma: 3.0, maxIterations: 5);
            token.ThrowIfCancellationRequested();

            // 2. Calcola Maschera Cometa (per proteggerla)
            using Mat cometMask = _segmentationEngine.ComputeCometMask(src, stats.MedianApprox, stats.StdDev, p);
            token.ThrowIfCancellationRequested();

            // 3. Calcola Maschera Stelle (escludendo la cometa)
            using Mat starMask = _segmentationEngine.ComputeStarMask(src, cometMask, stats.MedianApprox, stats.StdDev, p);
            token.ThrowIfCancellationRequested();

            // 4. Esegui Inpainting (Rimozione Stelle)
            // InpaintStars restituisce una NUOVA matrice clonata/modificata
            using Mat starlessImage = _inpaintingEngine.InpaintStars(src, starMask);
            token.ThrowIfCancellationRequested();

            // 5. Converti il risultato in Array per il renderer
            // Manteniamo la profondità di bit originale (Float o Double)
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
        // Operazione definita per ogni file nel batch
        Func<Mat, Mat, FitsHeader, int, Task> removalOp = async (src, dst, header, index) =>
        {
            await Task.Run(() =>
            {
                // 1. Statistiche
                var stats = _radiometryEngine.ComputeRobustStatistics(src, sigma: 3.0, maxIterations: 5);

                // 2. Calcolo Maschere
                using Mat cometMask = _segmentationEngine.ComputeCometMask(src, stats.MedianApprox, stats.StdDev, p);
                using Mat starMask = _segmentationEngine.ComputeStarMask(src, cometMask, stats.MedianApprox, stats.StdDev, p);

                // 3. Inpainting (Rimozione Stelle)
                using Mat starlessResult = _inpaintingEngine.InpaintStars(src, starMask);

                // 4. Copia il risultato nel buffer di destinazione (dst)
                // BatchProcessingService si occuperà di salvare 'dst' su disco.
                starlessResult.CopyTo(dst);

                // 5. Aggiornamento Header
                _metadataService.AddValue(header, "HISTORY", "KomaLab: Stars Removed");
                _metadataService.AddValue(header, "HISTORY", $"Params: Threshold={p.StarThresholdSigma}, Dilation={p.StarDilation}");
            }, token);
        };

        return await _batchService.ProcessFilesAsync(
            sourceFiles,
            "Starless", // Suffisso per i file salvati
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
            
            using Mat cometMask = _segmentationEngine.ComputeCometMask(src, stats.MedianApprox, stats.StdDev, p);
            using Mat starMask = _segmentationEngine.ComputeStarMask(src, cometMask, stats.MedianApprox, stats.StdDev, p);

            // Ritorna le maschere raw (byte) per la UI
            return (
                _converter.MatToRaw(starMask, FitsBitDepth.UInt8),
                _converter.MatToRaw(cometMask, FitsBitDepth.UInt8)
            );
        }, token);
    }
}