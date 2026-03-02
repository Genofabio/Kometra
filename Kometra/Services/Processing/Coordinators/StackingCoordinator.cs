using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Channels; 
using System.Threading.Tasks;
using Kometra.Models.Fits;
using Kometra.Models.Fits.Structure;
using Kometra.Models.Processing;
using Kometra.Models.Processing.Stacking;
using Kometra.Services.Fits;
using Kometra.Services.Fits.Conversion;
using Kometra.Services.Fits.Metadata;
using Kometra.Services.Processing.Engines;
using OpenCvSharp;

namespace Kometra.Services.Processing.Coordinators;

public class StackingCoordinator : IStackingCoordinator
{
    private readonly IFitsDataManager _dataManager;
    private readonly IFitsMetadataService _metadataService;
    private readonly IFitsOpenCvConverter _converter;
    private readonly IStackingEngine _stackingEngine;

    // Limite di precisione intera esatta per IEEE 754 Float (32-bit).
    private const double FloatPrecisionLimit = 16_777_216.0;

    public StackingCoordinator(
        IFitsDataManager dataManager,
        IFitsMetadataService metadataService,
        IFitsOpenCvConverter converter,
        IStackingEngine stackingEngine)
    {
        _dataManager = dataManager ?? throw new ArgumentNullException(nameof(dataManager));
        _metadataService = metadataService ?? throw new ArgumentNullException(nameof(metadataService));
        _converter = converter ?? throw new ArgumentNullException(nameof(converter));
        _stackingEngine = stackingEngine ?? throw new ArgumentNullException(nameof(stackingEngine));
    }

    public async Task<FitsFileReference> ExecuteStackingAsync(IEnumerable<FitsFileReference> sourceFiles, StackingMode mode)
    {
        var fileList = sourceFiles.ToList();
        if (fileList.Count < 2) 
            throw new InvalidOperationException("Sono necessarie almeno 2 immagini per eseguire lo stacking.");

        // 1. Analisi Input e Setup
        var firstFrameData = await _dataManager.GetDataAsync(fileList[0].FilePath);
        
        // [MODIFICA MEF] Accesso sicuro all'HDU
        var firstImageHdu = firstFrameData.FirstImageHdu ?? firstFrameData.PrimaryHdu;
        if (firstImageHdu == null) 
            throw new InvalidOperationException($"Il primo file ({fileList[0].FilePath}) non contiene immagini valide.");

        // Preferenza header modificato in RAM
        var baseHeader = fileList[0].ModifiedHeader ?? firstImageHdu.Header;
        int width = _metadataService.GetIntValue(baseHeader, "NAXIS1");
        int height = _metadataService.GetIntValue(baseHeader, "NAXIS2");

        // 2. Analisi Dinamica della Precisione
        bool useDoubleAccumulator = false;
        
        // Creiamo una Mat temporanea usando i dati dell'HDU
        using (var testMat = _converter.RawToMat(firstImageHdu.PixelData, 1, 0, null))
        {
            // A. Se l'input è già Double, restiamo in Double
            if (testMat.Depth() == MatType.CV_64F)
            {
                useDoubleAccumulator = true;
            }
            // B. Se facciamo SOMMA su dati Float/Int, controlliamo se serve passare a Double
            else if (mode == StackingMode.Sum)
            {
                double maxPixelValue = 0;

                // Tentativo 1: Leggere DATAMAX dall'header (veloce)
                double headerMax = _metadataService.GetDoubleValue(baseHeader, "DATAMAX", 0.0);
                
                if (headerMax > 0)
                {
                    maxPixelValue = headerMax;
                }
                else
                {
                    // Tentativo 2: Scansione reale del primo frame
                    testMat.MinMaxLoc(out _, out maxPixelValue);
                }

                // C. Calcolo del Rischio Overflow
                double estimatedTotalSum = maxPixelValue * fileList.Count * 1.2;

                if (estimatedTotalSum > FloatPrecisionLimit)
                {
                    useDoubleAccumulator = true;
                }
            }
        }
        
        Mat finalMat = null;

        // =================================================================================
        // STRATEGIA 1: MEDIANA
        // =================================================================================
        if (mode == StackingMode.Median)
        {
            finalMat = await _stackingEngine.ComputeMedianChunkedAsync(
                fileList, width, height, 
                async (fileRef, rect) => 
                {
                    var data = await _dataManager.GetDataAsync(fileRef.FilePath);
                    var imgHdu = data.FirstImageHdu ?? data.PrimaryHdu;
                    if (imgHdu == null) return new Mat(rect.Height, rect.Width, MatType.CV_32F, new Scalar(0));

                    var h = fileRef.ModifiedHeader ?? imgHdu.Header;
                    double bScale = _metadataService.GetDoubleValue(h, "BSCALE", 1.0);
                    double bZero = _metadataService.GetDoubleValue(h, "BZERO", 0.0);

                    using var fullMat = _converter.RawToMat(imgHdu.PixelData, bScale, bZero, targetDepth: null);
                    return fullMat.SubMat(rect).Clone();
                });
        }
        // =================================================================================
        // STRATEGIA 2: SOMMA/MEDIA
        // =================================================================================
        else 
        {
            if (_stackingEngine is StackingEngine engine)
            {
                engine.InitializeAccumulators(width, height, useDoubleAccumulator, out Mat accumulator, out Mat countMap);

                var channel = Channel.CreateBounded<Mat>(new BoundedChannelOptions(3)
                {
                    SingleWriter = true, SingleReader = true, FullMode = BoundedChannelFullMode.Wait
                });

                var producerTask = Task.Run(async () =>
                {
                    try
                    {
                        foreach (var fileRef in fileList)
                        {
                            var data = await _dataManager.GetDataAsync(fileRef.FilePath);
                            
                            // [MODIFICA MEF] Accesso sicuro all'HDU
                            var imgHdu = data.FirstImageHdu ?? data.PrimaryHdu;
                            if (imgHdu == null) continue;

                            var h = fileRef.ModifiedHeader ?? imgHdu.Header;
                            double bScale = _metadataService.GetDoubleValue(h, "BSCALE", 1.0);
                            double bZero = _metadataService.GetDoubleValue(h, "BZERO", 0.0);

                            var mat = _converter.RawToMat(imgHdu.PixelData, bScale, bZero, targetDepth: null);
                            await channel.Writer.WriteAsync(mat);
                        }
                    }
                    catch (Exception ex) { channel.Writer.Complete(ex); }
                    finally { channel.Writer.Complete(); }
                });

                try
                {
                    await foreach (var mat in channel.Reader.ReadAllAsync())
                    {
                        using (mat) engine.AccumulateFrame(accumulator, countMap, mat);
                    }
                    await producerTask;

                    if (mode == StackingMode.Average) engine.FinalizeAverage(accumulator, countMap);
                    
                    finalMat = accumulator;
                }
                catch
                {
                    if (finalMat == null) accumulator.Dispose();
                    throw;
                }
                finally { countMap.Dispose(); }
            }
            else
            {
                throw new InvalidOperationException("L'engine configurato non supporta lo stacking incrementale.");
            }
        }

        // =================================================================================
        // 3. FINALIZZAZIONE E SALVATAGGIO
        // =================================================================================
        try
        {
            using (finalMat) 
            {
                FitsBitDepth outputDepth = (finalMat.Depth() == MatType.CV_64F) 
                    ? FitsBitDepth.Double 
                    : FitsBitDepth.Float;

                var finalPixels = _converter.MatToRaw(finalMat, outputDepth);
                var finalHeader = _metadataService.CreateHeaderFromTemplate(baseHeader, finalPixels, outputDepth);

                _metadataService.SetValue(finalHeader, "NCOMBINE", fileList.Count, "Number of combined frames");
                _metadataService.SetValue(finalHeader, "STACKMET", mode.ToString().ToUpper(), "Stacking algorithm");
                _metadataService.SetValue(finalHeader, "DATE-PRO", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss"), "Processing timestamp");

                return await _dataManager.SaveAsTemporaryAsync(finalPixels, finalHeader, "StackResult");
            }
        }
        catch
        {
            if (finalMat != null && !finalMat.IsDisposed) finalMat.Dispose();
            throw;
        }
    }
}