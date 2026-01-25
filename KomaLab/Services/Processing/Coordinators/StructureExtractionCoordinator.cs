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

    // =======================================================================
    // 1. ANTEPRIMA (CALCOLO ASINCRONO)
    // =======================================================================
    
    public async Task<Array> CalculatePreviewDataAsync(
        FitsFileReference sourceFile,
        StructureExtractionMode mode,
        StructureExtractionParameters parameters)
    {
        // 1. Carica i dati Raw (Asincrono)
        var data = await _dataManager.GetDataAsync(sourceFile.FilePath);

        // 2. Conversione Raw -> Mat 
        using Mat src = _converter.RawToMat(data.PixelData);

        // 3. Esecuzione Logica Engine (Asincrona)
        using Mat dst = new Mat();
        // Passiamo un progresso nullo per l'anteprima, o potresti collegarlo a una mini-bar
        await RunEngineLogicAsync(src, dst, mode, parameters);

        // 4. Conversione Mat -> Array C# per la UI
        return _converter.MatToRaw(dst, FitsBitDepth.Double);
    }

    // =======================================================================
    // 2. BATCH (Elaborazione su file)
    // =======================================================================

    public async Task<List<string>> ExecuteBatchAsync(
        IEnumerable<FitsFileReference> sourceFiles,
        StructureExtractionMode mode,
        StructureExtractionParameters parameters,
        IProgress<BatchProgressReport>? progress = null,
        CancellationToken token = default)
    {
        // Definiamo l'operazione di processing che il BatchService eseguirà per ogni file
        // Poiché il BatchService è solitamente sincrono nel suo loop interno per file, 
        // usiamo .GetAwaiter().GetResult() o Task.Run se necessario, ma qui
        // la cosa migliore è rendere l'Action asincrona se il BatchService lo supporta.
        
        // Se il tuo IBatchProcessingService accetta un Func<..., Task>, usa quello.
        // Altrimenti, usiamo questa logica interna:
        Func<Mat, Mat, FitsHeader, int, Task> processOpAsync = async (src, dst, header, index) =>
        {
            using Mat processingResult = new Mat();

            // 1. Esecuzione Logica (Asincrona)
            await RunEngineLogicAsync(src, processingResult, mode, parameters);

            // 2. Gestione Output Mosaico vs Singolo
            if (processingResult.Size() != src.Size())
            {
                dst.Create(processingResult.Size(), src.Type());
                _metadataService.AddValue(header, "NAXIS1", processingResult.Cols, "Width");
                _metadataService.AddValue(header, "NAXIS2", processingResult.Rows, "Height");
            }

            // 3. Normalizzazione MinMax (0-65535) per il salvataggio
            Cv2.Normalize(processingResult, processingResult, 0, 65535, NormTypes.MinMax);
            
            // 4. Conversione verso dst
            processingResult.ConvertTo(dst, src.Type());

            // 5. Log Header
            UpdateHeaderHistory(header, mode, parameters);
        };

        // NOTA: Assicurati che il tuo BatchService supporti l'overload asincrono.
        // Se non lo supporta, dovrai chiamare processOpAsync(...).Wait() dentro l'Action.
        return await _batchService.ProcessFilesAsync(
            sourceFiles,
            $"Structure_{mode}", 
            async (s, d, h, i) => await processOpAsync(s, d, h, i),
            progress,
            token);
    }

    // =======================================================================
    // 3. LOGICA DI MAPPING (Mappata sui nuovi Task dell'Engine)
    // =======================================================================

    private async Task RunEngineLogicAsync(Mat src, Mat dst, StructureExtractionMode mode, StructureExtractionParameters p, IProgress<double>? progress = null)
    {
        switch (mode)
        {
            case StructureExtractionMode.LarsonSekaninaStandard:
                await _engine.ApplyLarsonSekaninaAsync(src, dst, p.RotationAngle, p.ShiftX, p.ShiftY, false);
                break;

            case StructureExtractionMode.LarsonSekaninaSymmetric:
                await _engine.ApplyLarsonSekaninaAsync(src, dst, p.RotationAngle, p.ShiftX, p.ShiftY, true);
                break;

            case StructureExtractionMode.UnsharpMaskingMedian:
                await _engine.ApplyUnsharpMaskingMedianAsync(src, dst, p.KernelSize, progress);
                break;

            case StructureExtractionMode.AdaptiveLaplacianRVSF:
                await _engine.ApplyAdaptiveRVSFAsync(src, dst, p.ParamA_1, p.ParamB_1, p.ParamN_1, p.UseLog, progress);
                break;

            case StructureExtractionMode.AdaptiveLaplacianMosaic:
                await _engine.ApplyRVSFMosaicAsync(src, dst,
                    (p.ParamA_1, p.ParamA_2),
                    (p.ParamB_1, p.ParamB_2),
                    (p.ParamN_1, p.ParamN_2),
                    p.UseLog);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Modalità di estrazione non supportata.");
        }
    }

    // =======================================================================
    // 4. LOGGING HEADER
    // =======================================================================

    private void UpdateHeaderHistory(FitsHeader header, StructureExtractionMode mode, StructureExtractionParameters p)
    {
        _metadataService.AddValue(header, "HISTORY", $"KomaLab: Structure Extraction Mode={mode}");

        string paramsLog = mode switch
        {
            StructureExtractionMode.LarsonSekaninaStandard or 
            StructureExtractionMode.LarsonSekaninaSymmetric => 
                $"Angle={p.RotationAngle:F2}, Shift=({p.ShiftX:F1}, {p.ShiftY:F1})",
            
            StructureExtractionMode.UnsharpMaskingMedian => 
                $"Kernel={p.KernelSize} px",
            
            StructureExtractionMode.AdaptiveLaplacianRVSF => 
                $"RVSF A={p.ParamA_1:F2}, B={p.ParamB_1:F2}, N={p.ParamN_1:F2}, Log={p.UseLog}",
            
            StructureExtractionMode.AdaptiveLaplacianMosaic => 
                $"Mosaic Ranges: A[{p.ParamA_1}-{p.ParamA_2}], B[{p.ParamB_1}-{p.ParamB_2}], N[{p.ParamN_1}-{p.ParamN_2}]",
            
            _ => ""
        };

        if (!string.IsNullOrEmpty(paramsLog))
        {
            _metadataService.AddValue(header, "HISTORY", $"Params: {paramsLog}");
        }
    }
}