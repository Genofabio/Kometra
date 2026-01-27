using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KomaLab.Models.Fits;
using KomaLab.Models.Fits.Structure;
using KomaLab.Models.Processing;
using KomaLab.Models.Processing.Enhancement;
using KomaLab.Services.Fits;
using KomaLab.Services.Fits.Conversion;
using KomaLab.Services.Fits.Metadata;
using KomaLab.Services.Processing.Batch;
using KomaLab.Services.Processing.Engines;
using KomaLab.Services.Processing.Engines.Enhancement;
using OpenCvSharp;

namespace KomaLab.Services.Processing.Coordinators;

public class ImageEnhancementCoordinator : IImageEnhancementCoordinator
{
    private readonly IBatchProcessingService _batchService;
    private readonly IFitsMetadataService _metadataService;
    private readonly IFitsDataManager _dataManager;
    private readonly IFitsOpenCvConverter _converter;

    private readonly IGradientRadialEngine _radialEngine;
    private readonly IStructureShapeEngine _shapeEngine;
    private readonly ILocalContrastEngine _contrastEngine;

    public ImageEnhancementCoordinator(
        IBatchProcessingService batchService,
        IFitsMetadataService metadataService,
        IFitsDataManager dataManager,
        IFitsOpenCvConverter converter,
        IGradientRadialEngine radialEngine,
        IStructureShapeEngine shapeEngine,
        ILocalContrastEngine contrastEngine)
    {
        _batchService = batchService;
        _metadataService = metadataService;
        _dataManager = dataManager;
        _converter = converter;
        _radialEngine = radialEngine;
        _shapeEngine = shapeEngine;
        _contrastEngine = contrastEngine;
    }

    public async Task<Array> CalculatePreviewDataAsync(
        FitsFileReference sourceFile,
        ImageEnhancementMode mode,
        ImageEnhancementParameters parameters,
        CancellationToken token = default) 
    {
        var data = await _dataManager.GetDataAsync(sourceFile.FilePath);
        token.ThrowIfCancellationRequested();
        
        // [MODIFICA MEF] Accesso sicuro all'HDU immagine
        var imageHdu = data.FirstImageHdu ?? data.PrimaryHdu;
        if (imageHdu == null) 
            throw new InvalidOperationException("No valid image found for preview.");

        // Usiamo i PixelData dell'HDU
        using Mat src = _converter.RawToMat(imageHdu.PixelData, 1.0, 0.0, FitsBitDepth.Double);
        using Mat dst = new Mat();
        
        // Passiamo il token alla logica
        await RunEngineLogicAsync(src, dst, mode, parameters, null, token);

        if (dst.Empty()) return imageHdu.PixelData;

        return _converter.MatToRaw(dst, FitsBitDepth.Double);
    }

    public async Task<List<string>> ExecuteBatchAsync(
        IEnumerable<FitsFileReference> sourceFiles,
        ImageEnhancementMode mode,
        ImageEnhancementParameters parameters,
        IProgress<BatchProgressReport>? progress = null,
        CancellationToken token = default)
    {
        Func<Mat, Mat, FitsHeader, int, Task> processOpAsync = async (src, dst, header, index) =>
        {
            // Check preventivo
            token.ThrowIfCancellationRequested();

            using Mat processingResult = new Mat();
            
            // Passiamo il token alla logica di elaborazione del singolo file
            await RunEngineLogicAsync(src, processingResult, mode, parameters, null, token);

            // Check post-elaborazione
            token.ThrowIfCancellationRequested();

            if (processingResult.Size() != src.Size())
            {
                dst.Create(processingResult.Size(), src.Type());
                _metadataService.AddValue(header, "NAXIS1", processingResult.Cols, "New Width");
                _metadataService.AddValue(header, "NAXIS2", processingResult.Rows, "New Height");
                
                if (mode == ImageEnhancementMode.InverseRho || mode == ImageEnhancementMode.AzimuthalAverage)
                     _metadataService.AddValue(header, "HISTORY", "Geometry: Radial/Polar Transform applied");
            }

            Cv2.Normalize(processingResult, processingResult, 0, 65535, NormTypes.MinMax);
            processingResult.ConvertTo(dst, src.Type());

            UpdateHeaderHistory(header, mode, parameters);
        };

        return await _batchService.ProcessFilesAsync(
            sourceFiles,
            $"Enhancement_{mode}", 
            processOpAsync,
            progress,
            token);
    }

    // =========================================================
    // DISPATCHER CENTRALE (Propaga il token agli engine)
    // =========================================================
    private async Task RunEngineLogicAsync(
        Mat src, Mat dst, 
        ImageEnhancementMode mode, 
        ImageEnhancementParameters p, 
        IProgress<double>? progress,
        CancellationToken token)
    {
        // Se l'operazione è già stata annullata, non avviamo l'engine
        token.ThrowIfCancellationRequested();

        switch (mode)
        {
            case ImageEnhancementMode.LarsonSekaninaStandard:
                await _radialEngine.ApplyLarsonSekaninaAsync(src, dst, p.RotationAngle, p.ShiftX, p.ShiftY, false); break;
            case ImageEnhancementMode.LarsonSekaninaSymmetric:
                await _radialEngine.ApplyLarsonSekaninaAsync(src, dst, p.RotationAngle, p.ShiftX, p.ShiftY, true); break;
            case ImageEnhancementMode.AdaptiveLaplacianRVSF:
                await _radialEngine.ApplyAdaptiveRVSFAsync(src, dst, p.ParamA_1, p.ParamB_1, p.ParamN_1, p.UseLog, progress); break;
            case ImageEnhancementMode.AdaptiveLaplacianMosaic:
                await _radialEngine.ApplyRVSFMosaicAsync(src, dst, (p.ParamA_1, p.ParamA_2), (p.ParamB_1, p.ParamB_2), (p.ParamN_1, p.ParamN_2), p.UseLog); break;
            
            case ImageEnhancementMode.InverseRho:
                // Passiamo il token a Task.Run
                await Task.Run(() => _radialEngine.ApplyInverseRho(src, dst, p.RadialSubsampling), token); break;

            case ImageEnhancementMode.AzimuthalAverage:
                await RunPolarFilterAsync(src, dst, polar => _radialEngine.ApplyAzimuthalAverage(polar, p.AzimuthalRejSigma), token); break;
            case ImageEnhancementMode.AzimuthalMedian:
                await RunPolarFilterAsync(src, dst, polar => _radialEngine.ApplyAzimuthalMedian(polar), token); break;
            case ImageEnhancementMode.AzimuthalRenormalization:
                await RunPolarFilterAsync(src, dst, polar => _radialEngine.ApplyAzimuthalRenormalization(polar, p.AzimuthalRejSigma, p.AzimuthalNormSigma), token); break;

            case ImageEnhancementMode.FrangiVesselnessFilter:
                await _shapeEngine.ApplyFrangiVesselnessAsync(src, dst, p.FrangiSigma, p.FrangiBeta, p.FrangiC, progress); break;
            case ImageEnhancementMode.StructureTensorCoherence:
                await _shapeEngine.ApplyStructureTensorEnhancementAsync(src, dst, p.TensorSigma, p.TensorRho, progress); break;
            case ImageEnhancementMode.WhiteTopHatExtraction:
                await _shapeEngine.ApplyWhiteTopHatAsync(src, dst, p.TopHatKernelSize); break;

            case ImageEnhancementMode.UnsharpMaskingMedian:
                await _contrastEngine.ApplyUnsharpMaskingMedianAsync(src, dst, p.KernelSize, progress); break;
            case ImageEnhancementMode.ClaheLocalContrast:
                await _contrastEngine.ApplyClaheAsync(src, dst, p.ClaheClipLimit, p.ClaheTileSize); break;
            case ImageEnhancementMode.AdaptiveLocalNormalization:
                await _contrastEngine.ApplyLocalNormalizationAsync(src, dst, p.LocalNormWindowSize, p.LocalNormIntensity, progress); break;

            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
        }
    }

    private async Task RunPolarFilterAsync(Mat src, Mat dst, Action<Mat> polarFilterAction, CancellationToken token)
    {
        // Check prima della trasformazione pesante
        token.ThrowIfCancellationRequested();

        await Task.Run(() =>
        {
            int nRad = Math.Min(src.Cols, src.Rows) / 2;
            int nTheta = nRad * 3;

            using Mat polar = _radialEngine.ToPolar(src, nRad, nTheta);
            
            // Check intermedio prima di applicare il filtro
            if (token.IsCancellationRequested) return;

            polarFilterAction(polar);

            // Check finale prima di tornare in cartesiano
            if (token.IsCancellationRequested) return;

            _radialEngine.FromPolar(polar, dst, src.Cols, src.Rows);
        }, token);
    }

    private void UpdateHeaderHistory(FitsHeader header, ImageEnhancementMode mode, ImageEnhancementParameters p)
    {
        _metadataService.AddValue(header, "HISTORY", $"KomaLab - Enhancement Mode={mode}");
        
        string paramsLog = mode switch
        {
            ImageEnhancementMode.LarsonSekaninaStandard or ImageEnhancementMode.LarsonSekaninaSymmetric => 
                $"Angle={p.RotationAngle:F2}, Shift=({p.ShiftX:F1}, {p.ShiftY:F1})",
            ImageEnhancementMode.AdaptiveLaplacianRVSF => $"RVSF A={p.ParamA_1}, B={p.ParamB_1}, N={p.ParamN_1}",
            ImageEnhancementMode.AzimuthalAverage => $"RejSig={p.AzimuthalRejSigma}",
            ImageEnhancementMode.FrangiVesselnessFilter => $"Sigma={p.FrangiSigma}, Beta={p.FrangiBeta}",
            ImageEnhancementMode.UnsharpMaskingMedian => $"Kernel={p.KernelSize}",
            ImageEnhancementMode.AdaptiveLocalNormalization => $"Win={p.LocalNormWindowSize}, Int={p.LocalNormIntensity}",
            _ => ""
        };

        if (!string.IsNullOrEmpty(paramsLog))
            _metadataService.AddValue(header, "HISTORY", $"KomaLab - Enhancement Params: {paramsLog}");
    }
}