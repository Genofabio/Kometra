using System;
using System.Collections.Generic;
using System.Linq;
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

public class StackingCoordinator : IStackingCoordinator
{
    private readonly IFitsDataManager _dataManager;
    private readonly IFitsMetadataService _metadataService;
    private readonly IFitsOpenCvConverter _converter;
    private readonly IStackingEngine _stackingEngine; // Nuovo Motore Puro

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

        // Monitoraggio risorse unmanaged
        var matsToDispose = new List<Mat>();

        try
        {
            // 1. Caricamento e Conversione
            // Sfruttiamo il DataManager che gestisce intelligentemente la cache
            foreach (var fileRef in fileList)
            {
                var data = await _dataManager.GetDataAsync(fileRef.FilePath);
                
                double bScale = _metadataService.GetDoubleValue(data.Header, "BSCALE", 1.0);
                double bZero = _metadataService.GetDoubleValue(data.Header, "BZERO", 0.0);
                
                var mat = _converter.RawToMat(data.PixelData, bScale, bZero);
                matsToDispose.Add(mat);
            }

            // 2. Delega del Calcolo (Pure Math)
            // L'Engine si occupa di multithreading e algoritmi (Somma, Media, Mediana)
            using var resultMat = await _stackingEngine.ComputeStackAsync(matsToDispose, mode);

            // 3. Preparazione Output Scientifico
            // Esportiamo a 64-bit (Double) per preservare la precisione dello stack
            var finalPixels = _converter.MatToRaw(resultMat, FitsBitDepth.Double);

            // 4. Gestione Metadati (Logica di Dominio)
            // Recuperiamo l'header del primo frame come template astrometrico
            var firstFrameData = await _dataManager.GetDataAsync(fileList[0].FilePath);
            var finalHeader = _metadataService.CreateHeaderFromTemplate(
                firstFrameData.Header, 
                finalPixels, 
                FitsBitDepth.Double
            );

            // Arricchimento storico del file FITS
            _metadataService.AddValue(finalHeader, "HISTORY", $"KomaLab Stacked {fileList.Count} frames");
            _metadataService.AddValue(finalHeader, "HISTORY", $"Algorithm: {mode}");
            _metadataService.AddValue(finalHeader, "DATE-PRO", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss"), "Processing UTC date");

            // 5. Persistenza Temporanea
            // Il DataManager salva il file fisico e ci restituisce il riferimento per la UI
            return await _dataManager.SaveAsTemporaryAsync(finalPixels, finalHeader, "StackResult");
        }
        finally
        {
            // Pulizia rigorosa della memoria unmanaged di OpenCV
            foreach (var mat in matsToDispose)
            {
                mat.Dispose();
            }
        }
    }
}