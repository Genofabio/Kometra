using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KomaLab.Models.Fits;
using KomaLab.Models.Fits.Structure;
using KomaLab.Models.Processing;
using KomaLab.Services.Fits;
using KomaLab.Services.Fits.Conversion;
using KomaLab.Services.Fits.Metadata;
using KomaLab.Services.Processing.Engines;
using OpenCvSharp;

namespace KomaLab.Services.Processing.Coordinators;

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
        _dataManager = dataManager;
        _metadata = metadata;
        _converter = converter;
        _stackingEngine = stackingEngine;
        _calibrationEngine = calibrationEngine;
    }

    public async Task<List<string>> ExecuteCalibrationAsync(
        IEnumerable<string> lightPaths,
        IEnumerable<string> darkPaths,
        IEnumerable<string> flatPaths,
        IEnumerable<string> biasPaths,
        IProgress<BatchProgressReport>? progress = null,
        CancellationToken token = default)
    {
        Debug.WriteLine("[CalibrationCoordinator] --- INIZIO PROCESSO ---");
        var resultPaths = new List<string>();
        var lights = lightPaths.ToList();
        
        if (!darkPaths.Any() && !flatPaths.Any() && !biasPaths.Any())
        {
            Debug.WriteLine("[Calibration] Nessuna calibrazione richiesta. Uso i file originali.");
            return lightPaths.ToList();
        }
        
        Debug.WriteLine($"[CalibrationCoordinator] Luci da elaborare: {lights.Count}");

        // 1. CREAZIONE MASTER (In RAM)
        Debug.WriteLine("[CalibrationCoordinator] Creazione Master Frames...");
        Mat? masterBias = await CreateMasterAsync(biasPaths, "Bias", token);
        Mat? masterDark = await CreateMasterAsync(darkPaths, "Dark", token);
        Mat? masterFlat = await CreateMasterAsync(flatPaths, "Flat", token);

        Debug.WriteLine($"[CalibrationCoordinator] Stato Master: Bias={(masterBias != null)}, Dark={(masterDark != null)}, Flat={(masterFlat != null)}");

        try
        {
            // 2. CICLO DI CALIBRAZIONE SUI LIGHT
            for (int i = 0; i < lights.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                string currentPath = lights[i];
                Debug.WriteLine($"[CalibrationCoordinator] ({i + 1}/{lights.Count}) Elaboro: {System.IO.Path.GetFileName(currentPath)}");

                progress?.Report(new BatchProgressReport(
                    i + 1, lights.Count, System.IO.Path.GetFileName(currentPath), (double)(i + 1) / lights.Count * 100));

                try
                {
                    // A. Caricamento Light
                    var data = await _dataManager.GetDataAsync(currentPath);
                    var header = _metadata.CloneHeader(data.Header);
                    
                    using var lightMat = _converter.RawToMat(data.PixelData, 
                        _metadata.GetDoubleValue(header, "BSCALE", 1.0),
                        _metadata.GetDoubleValue(header, "BZERO", 0.0));
                    
                    Debug.WriteLine($"[CalibrationCoordinator] Matrice Light creata: {lightMat.Width}x{lightMat.Height}, Tipo: {lightMat.Type()}");

                    // B. Calibrazione tramite Engine
                    using var calibratedMat = _calibrationEngine.ApplyCalibration(lightMat, masterDark, masterFlat, masterBias);
                    Debug.WriteLine($"[CalibrationCoordinator] Calibrazione completata. Tipo output: {calibratedMat.Type()}");

                    // C. Finalizzazione Header e Salvataggio Temporaneo
                    FitsBitDepth outputDepth = (calibratedMat.Depth() == MatType.CV_64F) 
                        ? FitsBitDepth.Double 
                        : FitsBitDepth.Float;

                    var calibratedPixels = _converter.MatToRaw(calibratedMat, outputDepth);
                    var finalHeader = _metadata.CreateHeaderFromTemplate(header, calibratedPixels, outputDepth);
                    
                    _metadata.SetValue(finalHeader, "DARKCORR", "COMPLETE", "Dark frame subtraction applied");
                    _metadata.SetValue(finalHeader, "FLATCORR", "COMPLETE", "Flat field correction applied");
                    _metadata.SetValue(finalHeader, "BIASCORR", "COMPLETE", "Bias frame subtraction applied");
                    
                    _metadata.AddValue(finalHeader, "HISTORY", $"KomaLab - Calibrated (D:{darkPaths.Any()} F:{flatPaths.Any()} B:{biasPaths.Any()})");

                    Debug.WriteLine("[CalibrationCoordinator] Tentativo di salvataggio file temporaneo...");
                    var fileRef = await _dataManager.SaveAsTemporaryAsync(calibratedPixels, finalHeader, "Calibrated");
                    
                    if (fileRef != null && !string.IsNullOrEmpty(fileRef.FilePath))
                    {
                        Debug.WriteLine($"[CalibrationCoordinator] Salvataggio riuscito: {fileRef.FilePath}");
                        resultPaths.Add(fileRef.FilePath);
                    }
                    else
                    {
                        Debug.WriteLine("[CalibrationCoordinator] ERRORE: SaveAsTemporaryAsync ha restituito un riferimento nullo o path vuoto.");
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[CalibrationCoordinator] ERRORE durante l'elaborazione di {currentPath}: {ex.Message}");
                    // Continuiamo con il prossimo file invece di rompere tutto il batch? 
                    // Per ora rilanciamo per capire il problema
                    throw; 
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CalibrationCoordinator] ERRORE CRITICO nel ciclo batch: {ex.Message}");
            throw;
        }
        finally
        {
            Debug.WriteLine("[CalibrationCoordinator] Cleanup Master frames.");
            masterBias?.Dispose();
            masterDark?.Dispose();
            masterFlat?.Dispose();
        }

        Debug.WriteLine($"[CalibrationCoordinator] --- FINE PROCESSO --- Risultati: {resultPaths.Count}");
        return resultPaths;
    }

    private async Task<Mat?> CreateMasterAsync(IEnumerable<string> paths, string type, CancellationToken token)
    {
        var pathList = paths.ToList();
        if (!pathList.Any()) 
        {
            Debug.WriteLine($"[CalibrationCoordinator] Nessun file per Master {type}.");
            return null;
        }

        Debug.WriteLine($"[CalibrationCoordinator] Generazione Master {type} da {pathList.Count} file...");
        var mats = new List<Mat>();
        try
        {
            foreach (var p in pathList)
            {
                var data = await _dataManager.GetDataAsync(p);
                mats.Add(_converter.RawToMat(data.PixelData, 
                    _metadata.GetDoubleValue(data.Header, "BSCALE", 1.0),
                    _metadata.GetDoubleValue(data.Header, "BZERO", 0.0)));
            }

            // Usiamo l'engine di stacking esistente per fare la Media
            var master = await _stackingEngine.ComputeStackAsync(mats, StackingMode.Average);
            Debug.WriteLine($"[CalibrationCoordinator] Master {type} creato con successo.");
            return master;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CalibrationCoordinator] ERRORE durante creazione Master {type}: {ex.Message}");
            throw;
        }
        finally
        {
            foreach (var m in mats) m.Dispose();
        }
    }
}