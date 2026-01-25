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

public class RadialEnhancementCoordinator : IRadialEnhancementCoordinator
{
    private readonly IBatchProcessingService _batchService;
    private readonly IRadialEnhancementEngine _engine;
    private readonly IFitsDataManager _dataManager;
    private readonly IFitsMetadataService _metadataService;
    private readonly IFitsOpenCvConverter _converter; 

    public RadialEnhancementCoordinator(
        IBatchProcessingService batchService,
        IRadialEnhancementEngine engine,
        IFitsDataManager dataManager,
        IFitsMetadataService metadataService,
        IFitsOpenCvConverter converter)
    {
        _batchService = batchService;
        _engine = engine;
        _dataManager = dataManager;
        _metadataService = metadataService;
        _converter = converter;
    }

    public async Task<FitsHeader> GetFileMetadataAsync(FitsFileReference file)
    {
        // Priorità all'header modificato in sessione, altrimenti legge da disco
        return file.ModifiedHeader ?? await _dataManager.GetHeaderOnlyAsync(file.FilePath);
    }

    // =======================================================================
    // 1. ANTEPRIMA (Calcolo in RAM -> Array per Renderer Swapping)
    // =======================================================================

    public async Task<Array> CalculatePreviewDataAsync(
        FitsFileReference sourceFile,
        RadialEnhancementMode mode,
        int nRad, int nTheta, double rejSig, double nSig)
    {
        // Eseguiamo in un Task per non bloccare il thread UI durante la matematica
        return await Task.Run(async () =>
        {
            // A. Carichiamo i dati originali
            var data = await _dataManager.GetDataAsync(sourceFile.FilePath);
            
            // B. Recuperiamo parametri di scala fisici (BZERO/BSCALE)
            var header = sourceFile.ModifiedHeader ?? data.Header;
            double bScale = _metadataService.GetDoubleValue(header, "BSCALE", 1.0);
            double bZero = _metadataService.GetDoubleValue(header, "BZERO", 0.0);

            // C. Convertiamo in Matrice OpenCV a 32-bit (Float)
            // L'Engine matematico lavora in Float32 per massima efficienza
            using Mat src = _converter.RawToMat(data.PixelData, bScale, bZero, FitsBitDepth.Float);
            
            // D. Prepariamo la destinazione
            using Mat dst = new Mat(src.Size(), MatType.CV_32F);

            // E. Eseguiamo la logica condivisa
            RunEngineLogic(src, dst, mode, nRad, nTheta, rejSig, nSig);

            // F. Riconvertiamo in Array C# (Float) da passare alla Factory del Renderer
            return _converter.MatToRaw(dst, FitsBitDepth.Float);
        });
    }

    // =======================================================================
    // 2. BATCH (Calcolo -> Disco)
    // =======================================================================

    public async Task<List<string>> ExecuteEnhancementAsync(
        IEnumerable<FitsFileReference> sourceFiles,
        RadialEnhancementMode mode,
        int nRad, int nTheta, double rejSig, double nSig,
        IProgress<BatchProgressReport>? progress = null,
        CancellationToken token = default)
    {
        // Definiamo il processore che verrà chiamato per ogni file dal BatchService
        Action<Mat, Mat, FitsHeader, int> processor = (src, dst, header, index) =>
        {
            // NOTA TECNICA: 
            // Il BatchService fornisce matrici Double (64F) per preservare la precisione.
            // L'Engine radiale è ottimizzato per Float (32F) per velocità nei filtri convolutivi.
            // Qui facciamo il ponte tra i due mondi.
            
            // 1. Input: Double -> Float
            using Mat srcFloat = new Mat();
            src.ConvertTo(srcFloat, MatType.CV_32F);

            // 2. Output: Float (buffer di lavoro)
            using Mat dstFloat = new Mat(src.Size(), MatType.CV_32F);

            // 3. Logica Core
            RunEngineLogic(srcFloat, dstFloat, mode, nRad, nTheta, rejSig, nSig);

            // 4. Output: Float -> Double (per riempire la matrice di destinazione del batch service)
            dstFloat.ConvertTo(dst, src.Type()); 

            // 5. Aggiornamento Header (History)
            UpdateHeaderHistory(header, mode, nRad, nTheta, rejSig);
        };

        return await _batchService.ProcessFilesAsync(
            sourceFiles,
            "RadialEnhanced", // Nome cartella temporanea
            processor,
            progress,
            token);
    }

    // =======================================================================
    // 3. LOGICA CONDIVISA (Engine Bridge)
    // =======================================================================

    private void RunEngineLogic(Mat src, Mat dst, RadialEnhancementMode mode, int nRad, int nTheta, double rejSig, double nSig)
    {
        switch (mode)
        {
            case RadialEnhancementMode.InverseRho:
                // Operazione diretta Cartesiana -> Cartesiana
                _engine.ApplyInverseRho(src, dst);
                break;

            case RadialEnhancementMode.AzimuthalAverage:
            case RadialEnhancementMode.AzimuthalMedian:
            case RadialEnhancementMode.AzimuthalRenormalization:
                // Workflow Polare:
                // 1. Cartesiana -> Polare (Warp)
                using (Mat polar = _engine.ToPolar(src, nRad, nTheta))
                {
                    // 2. Filtro in spazio polare (in-place)
                    if (mode == RadialEnhancementMode.AzimuthalAverage)
                        _engine.ApplyAzimuthalAverage(polar, rejSig);
                    else if (mode == RadialEnhancementMode.AzimuthalMedian)
                        _engine.ApplyAzimuthalMedian(polar);
                    else
                        _engine.ApplyAzimuthalRenormalization(polar, rejSig, nSig);

                    // 3. Polare -> Cartesiana (Unwarp)
                    _engine.FromPolar(polar, dst, src.Cols, src.Rows);
                }
                break;
        }
    }

    private void UpdateHeaderHistory(FitsHeader header, RadialEnhancementMode mode, int nRad, int nTheta, double rejSig)
    {
        _metadataService.AddValue(header, "HISTORY", $"KomaLab: Radial Enhancement Mode={mode}");
        
        if (mode != RadialEnhancementMode.InverseRho)
        {
            string p = FormattableString.Invariant($"nRad={nRad}, nTheta={nTheta}, rejSig={rejSig:F1}");
            _metadataService.AddValue(header, "HISTORY", $"Params: {p}");
        }
    }
}