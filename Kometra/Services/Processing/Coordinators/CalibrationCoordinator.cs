using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kometra.Models.Fits;
using Kometra.Models.Fits.Structure;
using Kometra.Models.Processing;
using Kometra.Models.Processing.Batch;
using Kometra.Models.Processing.Stacking;
using Kometra.Services.Fits;
using Kometra.Services.Fits.Conversion;
using Kometra.Services.Fits.Metadata;
using Kometra.Services.Processing.Engines;
using OpenCvSharp;

namespace Kometra.Services.Processing.Coordinators;

public class CalibrationCoordinator : ICalibrationCoordinator
{
    private readonly IFitsDataManager _dataManager;
    private readonly IFitsMetadataService _metadata;
    private readonly IFitsOpenCvConverter _converter;
    private readonly IStackingEngine _stackingEngine;
    private readonly ICalibrationEngine _calibrationEngine;

    public CalibrationCoordinator(
        IFitsDataManager dataManager,
        IFitsMetadataService metadata,
        IFitsOpenCvConverter converter,
        IStackingEngine stackingEngine,
        ICalibrationEngine calibrationEngine)
    {
        _dataManager = dataManager ?? throw new ArgumentNullException(nameof(dataManager));
        _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        _converter = converter ?? throw new ArgumentNullException(nameof(converter));
        _stackingEngine = stackingEngine ?? throw new ArgumentNullException(nameof(stackingEngine));
        _calibrationEngine = calibrationEngine ?? throw new ArgumentNullException(nameof(calibrationEngine));
    }

    public async Task<List<string>> ExecuteCalibrationAsync(
        IEnumerable<string> lightPaths,
        IEnumerable<string> darkPaths,
        IEnumerable<string> flatPaths,
        IEnumerable<string> biasPaths,
        IProgress<BatchProgressReport>? progress = null,
        CancellationToken token = default)
    {
        var resultPaths = new List<string>();
        
        // Stabilizziamo le liste e contiamo gli elementi per il report sintetico
        var lights = lightPaths.ToList();
        var darks = darkPaths.ToList();
        var flats = flatPaths.ToList();
        var biases = biasPaths.ToList();
        
        // Se non c'è nulla da calibrare, restituiamo i file originali
        if (darks.Count == 0 && flats.Count == 0 && biases.Count == 0)
        {
            return lights;
        }
        
        // 1. CREAZIONE MASTER (In RAM)
        // Nota: Qui potremmo implementare una cache se i master fossero riutilizzabili
        Mat? masterBias = await CreateMasterAsync(biases, token);
        Mat? masterDark = await CreateMasterAsync(darks, token);
        Mat? masterFlat = await CreateMasterAsync(flats, token);

        try
        {
            // 2. CICLO DI CALIBRAZIONE SUI LIGHT
            for (int i = 0; i < lights.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                string currentPath = lights[i];
                
                progress?.Report(new BatchProgressReport(
                    i + 1, lights.Count, System.IO.Path.GetFileName(currentPath), (double)(i + 1) / lights.Count * 100));

                try
                {
                    // A. Caricamento Light
                    var data = await _dataManager.GetDataAsync(currentPath);
                    
                    var imageHdu = data.FirstImageHdu ?? data.PrimaryHdu;
                    if (imageHdu == null) continue;

                    // Cloniamo l'header originale
                    var header = _metadata.CloneHeader(imageHdu.Header);
                    
                    // Usiamo i PixelData dell'HDU
                    using var lightMat = _converter.RawToMat(imageHdu.PixelData, 
                        _metadata.GetDoubleValue(header, "BSCALE", 1.0),
                        _metadata.GetDoubleValue(header, "BZERO", 0.0));
                    
                    // B. Calibrazione effettiva
                    using var calibratedMat = _calibrationEngine.ApplyCalibration(lightMat, masterDark, masterFlat, masterBias);

                    // C. Finalizzazione
                    FitsBitDepth outputDepth = (calibratedMat.Depth() == MatType.CV_64F) 
                        ? FitsBitDepth.Double 
                        : FitsBitDepth.Float;

                    var calibratedPixels = _converter.MatToRaw(calibratedMat, outputDepth);
                    var finalHeader = _metadata.CreateHeaderFromTemplate(header, calibratedPixels, outputDepth);
                    
                    // --- AGGIUNTA METADATI STANDARD (NOAO/IRAF CONVENTION) ---
                    // Usiamo 'T' (True) come da standard FITS per i booleani
                    
                    if (masterBias != null)
                    {
                        _metadata.SetValue(finalHeader, "ZEROCORR", "T", "Zero/Bias correction applied");
                        _metadata.AddValue(finalHeader, "HISTORY", $"Bias correction applied using Master Bias ({biases.Count} frames)");
                    }

                    if (masterDark != null)
                    {
                        _metadata.SetValue(finalHeader, "DARKCORR", "T", "Dark frame subtraction applied");
                        _metadata.AddValue(finalHeader, "HISTORY", $"Dark subtraction applied using Master Dark ({darks.Count} frames)");
                    }

                    if (masterFlat != null)
                    {
                        _metadata.SetValue(finalHeader, "FLATCORR", "T", "Flat field correction applied");
                        _metadata.AddValue(finalHeader, "HISTORY", $"Flat field correction applied using Master Flat ({flats.Count} frames)");
                    }
                    
                    _metadata.AddValue(finalHeader, "HISTORY", "Calibration performed by Kometra");

                    // D. Salvataggio
                    var fileRef = await _dataManager.SaveAsTemporaryAsync(calibratedPixels, finalHeader, "Calibrated");
                    
                    if (fileRef != null && !string.IsNullOrEmpty(fileRef.FilePath))
                    {
                        resultPaths.Add(fileRef.FilePath);
                    }
                }
                catch (Exception)
                {
                    // Continua con il prossimo file in caso di errore su uno singolo
                    throw; 
                }
            }
        }
        finally
        {
            // Pulizia risorse OpenCV
            masterBias?.Dispose();
            masterDark?.Dispose();
            masterFlat?.Dispose();
        }

        return resultPaths;
    }

    private async Task<Mat?> CreateMasterAsync(List<string> paths, CancellationToken token)
    {
        if (paths.Count == 0) return null;

        var mats = new List<Mat>();
        try
        {
            foreach (var p in paths)
            {
                var data = await _dataManager.GetDataAsync(p);
                var imageHdu = data.FirstImageHdu ?? data.PrimaryHdu;
                if (imageHdu == null) continue;

                mats.Add(_converter.RawToMat(imageHdu.PixelData, 
                    _metadata.GetDoubleValue(imageHdu.Header, "BSCALE", 1.0),
                    _metadata.GetDoubleValue(imageHdu.Header, "BZERO", 0.0)));
            }

            if (mats.Count == 0) return null;

            // Per i Master di calibrazione si usa tipicamente la Media (o la Mediana)
            return await _stackingEngine.ComputeStackAsync(mats, StackingMode.Average);
        }
        finally
        {
            foreach (var m in mats) m.Dispose();
        }
    }
}