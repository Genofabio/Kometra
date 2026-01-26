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

    // I 3 Nuovi Engine Specializzati
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
        ImageEnhancementParameters parameters)
    {
        var data = await _dataManager.GetDataAsync(sourceFile.FilePath);
        
        // Usiamo sempre Double per la massima precisione nell'anteprima
        // (Il convertitore gestirà l'ottimizzazione se l'input è float)
        using Mat src = _converter.RawToMat(data.PixelData, 1.0, 0.0, FitsBitDepth.Double);
        using Mat dst = new Mat();
        
        // Eseguiamo la logica dell'engine appropriato
        await RunEngineLogicAsync(src, dst, mode, parameters);

        // Se l'operazione ha fallito o la matrice è vuota, restituisci l'originale
        if (dst.Empty()) return data.PixelData;

        return _converter.MatToRaw(dst, FitsBitDepth.Double);
    }

    public async Task<List<string>> ExecuteBatchAsync(
        IEnumerable<FitsFileReference> sourceFiles,
        ImageEnhancementMode mode,
        ImageEnhancementParameters parameters,
        IProgress<BatchProgressReport>? progress = null,
        CancellationToken token = default)
    {
        // Definiamo l'operazione che verrà eseguita per ogni file
        Func<Mat, Mat, FitsHeader, int, Task> processOpAsync = async (src, dst, header, index) =>
        {
            using Mat processingResult = new Mat();
            
            // Eseguiamo il motore specifico
            await RunEngineLogicAsync(src, processingResult, mode, parameters, progress: null);

            // Gestione ridimensionamento (es. Mosaic o Polar Transforms)
            if (processingResult.Size() != src.Size())
            {
                dst.Create(processingResult.Size(), src.Type());
                _metadataService.AddValue(header, "NAXIS1", processingResult.Cols, "New Width");
                _metadataService.AddValue(header, "NAXIS2", processingResult.Rows, "New Height");
                
                // Se è una trasformazione polare, aggiorniamo la history
                if (mode == ImageEnhancementMode.InverseRho || mode == ImageEnhancementMode.AzimuthalAverage)
                     _metadataService.AddValue(header, "HISTORY", "Geometry: Radial/Polar Transform applied");
            }

            // Normalizzazione a 16-bit per il salvataggio FITS sicuro
            // Molti algoritmi (Frangi, LSN) producono float fuori scala o negativi.
            // La normalizzazione MinMax 0-65535 garantisce che il FITS sia visibile.
            Cv2.Normalize(processingResult, processingResult, 0, 65535, NormTypes.MinMax);
            
            // Convertiamo nel tipo della matrice di destinazione (di solito Double dal BatchService)
            processingResult.ConvertTo(dst, src.Type());

            UpdateHeaderHistory(header, mode, parameters);
        };

        // Chiamata al Batch Service (usa il nuovo overload asincrono che abbiamo creato)
        return await _batchService.ProcessFilesAsync(
            sourceFiles,
            $"Enhancement_{mode}", 
            processOpAsync,
            progress,
            token);
    }

    // =========================================================
    // DISPATCHER CENTRALE (Switch Mode -> Engine)
    // =========================================================
    private async Task RunEngineLogicAsync(
        Mat src, Mat dst, 
        ImageEnhancementMode mode, 
        ImageEnhancementParameters p, 
        IProgress<double>? progress = null)
    {
        switch (mode)
        {
            // --- GRUPPO 1: GRADIENT & RADIAL (Ieri: Structure & Radial Engine) ---
            case ImageEnhancementMode.LarsonSekaninaStandard:
                await _radialEngine.ApplyLarsonSekaninaAsync(src, dst, p.RotationAngle, p.ShiftX, p.ShiftY, false); break;
            case ImageEnhancementMode.LarsonSekaninaSymmetric:
                await _radialEngine.ApplyLarsonSekaninaAsync(src, dst, p.RotationAngle, p.ShiftX, p.ShiftY, true); break;
            case ImageEnhancementMode.AdaptiveLaplacianRVSF:
                await _radialEngine.ApplyAdaptiveRVSFAsync(src, dst, p.ParamA_1, p.ParamB_1, p.ParamN_1, p.UseLog, progress); break;
            case ImageEnhancementMode.AdaptiveLaplacianMosaic:
                await _radialEngine.ApplyRVSFMosaicAsync(src, dst, (p.ParamA_1, p.ParamA_2), (p.ParamB_1, p.ParamB_2), (p.ParamN_1, p.ParamN_2), p.UseLog); break;
            
            case ImageEnhancementMode.InverseRho:
                await Task.Run(() => _radialEngine.ApplyInverseRho(src, dst, p.RadialSubsampling)); break;

            case ImageEnhancementMode.AzimuthalAverage:
                await RunPolarFilterAsync(src, dst, polar => _radialEngine.ApplyAzimuthalAverage(polar, p.AzimuthalRejSigma)); break;
            case ImageEnhancementMode.AzimuthalMedian:
                await RunPolarFilterAsync(src, dst, polar => _radialEngine.ApplyAzimuthalMedian(polar)); break;
            case ImageEnhancementMode.AzimuthalRenormalization:
                await RunPolarFilterAsync(src, dst, polar => _radialEngine.ApplyAzimuthalRenormalization(polar, p.AzimuthalRejSigma, p.AzimuthalNormSigma)); break;

            // --- GRUPPO 2: SHAPE & FEATURES ---
            case ImageEnhancementMode.FrangiVesselnessFilter:
                await _shapeEngine.ApplyFrangiVesselnessAsync(src, dst, p.FrangiSigma, p.FrangiBeta, p.FrangiC, progress); break;
            case ImageEnhancementMode.StructureTensorCoherence:
                await _shapeEngine.ApplyStructureTensorEnhancementAsync(src, dst, p.TensorSigma, p.TensorRho, progress); break;
            case ImageEnhancementMode.WhiteTopHatExtraction:
                await _shapeEngine.ApplyWhiteTopHatAsync(src, dst, p.TopHatKernelSize); break;

            // --- GRUPPO 3: LOCAL CONTRAST ---
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

    /// <summary>
    /// Helper per eseguire filtri nel dominio polare: Cartesiano -> Polare -> Filtro -> Cartesiano
    /// </summary>
    private async Task RunPolarFilterAsync(Mat src, Mat dst, Action<Mat> polarFilterAction)
    {
        await Task.Run(() =>
        {
            // Calcolo dimensioni ottimali polari (raggio = metà lato minore)
            int nRad = Math.Min(src.Cols, src.Rows) / 2;
            int nTheta = nRad * 3; // Sovracampionamento angolare x3 per qualità

            using Mat polar = _radialEngine.ToPolar(src, nRad, nTheta);
            
            // Applica il filtro sull'immagine polare
            polarFilterAction(polar);

            // Torna in cartesiano
            _radialEngine.FromPolar(polar, dst, src.Cols, src.Rows);
        });
    }

    private void UpdateHeaderHistory(FitsHeader header, ImageEnhancementMode mode, ImageEnhancementParameters p)
    {
        _metadataService.AddValue(header, "HISTORY", $"KomaLab: Enhancement Mode={mode}");
        
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
            _metadataService.AddValue(header, "HISTORY", $"Params: {paramsLog}");
    }
}