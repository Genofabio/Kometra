using System;
using System.Collections.Generic;
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
using OpenCvSharp;

namespace KomaLab.Services.Processing.Coordinators;

public class StructureExtractionCoordinator : IStructureExtractionCoordinator
{
    private readonly IBatchProcessingService _batchService;
    private readonly IStructureExtractionEngine _engine;
    private readonly IFitsMetadataService _metadataService;
    private readonly IFitsDataManager _dataManager;
    private readonly IFitsOpenCvConverter _converter;

    public StructureExtractionCoordinator(
        IBatchProcessingService batchService,
        IStructureExtractionEngine engine,
        IFitsMetadataService metadataService,
        IFitsDataManager dataManager,
        IFitsOpenCvConverter converter)
    {
        _batchService = batchService;
        _engine = engine;
        _metadataService = metadataService;
        _dataManager = dataManager;
        _converter = converter;
    }

    public async Task<Array> CalculatePreviewDataAsync(
        FitsFileReference sourceFile,
        StructureExtractionMode mode,
        StructureExtractionParameters parameters)
    {
        var data = await _dataManager.GetDataAsync(sourceFile.FilePath);
        using Mat src = _converter.RawToMat(data.PixelData);
        using Mat dst = new Mat();
        
        await RunEngineLogicAsync(src, dst, mode, parameters);

        return _converter.MatToRaw(dst, FitsBitDepth.Double);
    }

    // File: StructureExtractionCoordinator.cs

    public async Task<List<string>> ExecuteBatchAsync(
        IEnumerable<FitsFileReference> sourceFiles,
        StructureExtractionMode mode,
        StructureExtractionParameters parameters,
        IProgress<BatchProgressReport>? progress = null,
        CancellationToken token = default)
    {
        // 1. Definisci la logica di elaborazione asincrona
        // La firma corrisponde esattamente al nuovo overload: Func<Mat, Mat, FitsHeader, int, Task>
        Func<Mat, Mat, FitsHeader, int, Task> processOpAsync = async (src, dst, header, index) =>
        {
            using Mat processingResult = new Mat();
        
            // Eseguiamo il motore (asincrono)
            await RunEngineLogicAsync(src, processingResult, mode, parameters);

            // Gestione ridimensionamento (es. Mosaic)
            if (processingResult.Size() != src.Size())
            {
                dst.Create(processingResult.Size(), src.Type());
                _metadataService.AddValue(header, "NAXIS1", processingResult.Cols, "Width");
                _metadataService.AddValue(header, "NAXIS2", processingResult.Rows, "Height");
            }

            // Normalizzazione e conversione finale
            Cv2.Normalize(processingResult, processingResult, 0, 65535, NormTypes.MinMax);
            processingResult.ConvertTo(dst, src.Type());

            UpdateHeaderHistory(header, mode, parameters);
        };

        // 2. CHIAMATA AL SERVIZIO (Nativa Async)
        // Grazie all'overload che abbiamo creato nel Service, passiamo direttamente 'processOpAsync'.
        // Il Service farà l'await corretto, mantenendo aperto il 'using Mat src' finché necessario.
        return await _batchService.ProcessFilesAsync(
            sourceFiles,
            $"Structure_{mode}", 
            processOpAsync, // Il compilatore sceglie automaticamente l'overload Func<..., Task>
            progress,
            token);
    }

    private async Task RunEngineLogicAsync(Mat src, Mat dst, StructureExtractionMode mode, StructureExtractionParameters p, IProgress<double>? progress = null)
    {
        switch (mode)
        {
            case StructureExtractionMode.LarsonSekaninaStandard:
                await _engine.ApplyLarsonSekaninaAsync(src, dst, p.RotationAngle, p.ShiftX, p.ShiftY, false); break;
            case StructureExtractionMode.LarsonSekaninaSymmetric:
                await _engine.ApplyLarsonSekaninaAsync(src, dst, p.RotationAngle, p.ShiftX, p.ShiftY, true); break;
            case StructureExtractionMode.UnsharpMaskingMedian:
                await _engine.ApplyUnsharpMaskingMedianAsync(src, dst, p.KernelSize, progress); break;
            case StructureExtractionMode.AdaptiveLaplacianRVSF:
                await _engine.ApplyAdaptiveRVSFAsync(src, dst, p.ParamA_1, p.ParamB_1, p.ParamN_1, p.UseLog, progress); break;
            case StructureExtractionMode.AdaptiveLaplacianMosaic:
                await _engine.ApplyRVSFMosaicAsync(src, dst, (p.ParamA_1, p.ParamA_2), (p.ParamB_1, p.ParamB_2), (p.ParamN_1, p.ParamN_2), p.UseLog); break;
            
            // --- NUOVI MAPPING ---
            case StructureExtractionMode.FrangiVesselnessFilter:
                await _engine.ApplyFrangiVesselnessAsync(src, dst, p.FrangiSigma, p.FrangiBeta, p.FrangiC, progress); break;
            case StructureExtractionMode.StructureTensorCoherence:
                await _engine.ApplyStructureTensorEnhancementAsync(src, dst, p.TensorSigma, p.TensorRho, progress); break;
            case StructureExtractionMode.WhiteTopHatExtraction:
                await _engine.ApplyWhiteTopHatAsync(src, dst, p.TopHatKernelSize); break;
            case StructureExtractionMode.ClaheLocalContrast:
                await _engine.ApplyClaheAsync(src, dst, p.ClaheClipLimit, p.ClaheTileSize); break;
            case StructureExtractionMode.AdaptiveLocalNormalization:
                await _engine.ApplyLocalNormalizationAsync(src, dst, p.LocalNormWindowSize, p.LocalNormIntensity, progress); break;

            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
        }
    }

    private void UpdateHeaderHistory(FitsHeader header, StructureExtractionMode mode, StructureExtractionParameters p)
    {
        _metadataService.AddValue(header, "HISTORY", $"KomaLab: Structure Extraction Mode={mode}");

        string paramsLog = mode switch
        {
            StructureExtractionMode.LarsonSekaninaStandard or StructureExtractionMode.LarsonSekaninaSymmetric => 
                $"Angle={p.RotationAngle:F2}, Shift=({p.ShiftX:F1}, {p.ShiftY:F1})",
            StructureExtractionMode.UnsharpMaskingMedian => $"Kernel={p.KernelSize} px",
            StructureExtractionMode.AdaptiveLaplacianRVSF => $"RVSF A={p.ParamA_1:F2}, B={p.ParamB_1:F2}, N={p.ParamN_1:F2}, Log={p.UseLog}",
            StructureExtractionMode.FrangiVesselnessFilter => $"Sigma={p.FrangiSigma:F2}, Beta={p.FrangiBeta:F2}, C={p.FrangiC:F4}",
            StructureExtractionMode.StructureTensorCoherence => $"Sigma={p.TensorSigma}, Rho={p.TensorRho}",
            StructureExtractionMode.WhiteTopHatExtraction => $"Kernel={p.TopHatKernelSize} px",
            StructureExtractionMode.ClaheLocalContrast => $"Clip={p.ClaheClipLimit:F1}, Tiles={p.ClaheTileSize}",
            StructureExtractionMode.AdaptiveLocalNormalization => $"Window={p.LocalNormWindowSize}, Intensity={p.LocalNormIntensity:F1}",
            _ => ""
        };

        if (!string.IsNullOrEmpty(paramsLog))
            _metadataService.AddValue(header, "HISTORY", $"Params: {paramsLog}");
    }
}