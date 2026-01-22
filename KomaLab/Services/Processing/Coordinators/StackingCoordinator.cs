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
    private readonly IStackingEngine _stackingEngine;

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

        var matsToDispose = new List<Mat>();

        try
        {
            // 1. Caricamento e Conversione
            foreach (var fileRef in fileList)
            {
                var data = await _dataManager.GetDataAsync(fileRef.FilePath);
                // Usa l'header modificato se presente (es. dopo allineamento), altrimenti quello su disco
                var headerToUse = fileRef.ModifiedHeader ?? data.Header; 
                
                double bScale = _metadataService.GetDoubleValue(headerToUse, "BSCALE", 1.0);
                double bZero = _metadataService.GetDoubleValue(headerToUse, "BZERO", 0.0);
                
                // Usiamo Double per la massima precisione durante la somma
                var mat = _converter.RawToMat(data.PixelData, bScale, bZero, FitsBitDepth.Double);
                matsToDispose.Add(mat);
            }

            // 2. Calcolo Stacking (Somma/Media/Mediana)
            using var resultMat = await _stackingEngine.ComputeStackAsync(matsToDispose, mode);
            var finalPixels = _converter.MatToRaw(resultMat, FitsBitDepth.Double);

            // 3. Preparazione Header
            var firstFrameData = await _dataManager.GetDataAsync(fileList[0].FilePath);
            
            // NOTA: CreateHeaderFromTemplate ora imposta automaticamente:
            // - DATE (Creazione file fisico)
            // - CREATOR (KomaLab)
            // - ORIGIN (Copia dal sorgente o default)
            // - BITPIX, NAXIS, BSCALE, BZERO
            var finalHeader = _metadataService.CreateHeaderFromTemplate(
                fileList[0].ModifiedHeader ?? firstFrameData.Header, 
                finalPixels, 
                FitsBitDepth.Double
            );

            // --- CHIAVI SPECIFICHE DELLO STACKING ---
            
            // NCOMBINE: Quante immagini compongono questo stack?
            _metadataService.SetValue(finalHeader, "NCOMBINE", fileList.Count, "KomaLab - Number of combined frames");
            
            // STACKMET: Quale algoritmo matematico è stato usato?
            _metadataService.SetValue(finalHeader, "STACKMET", mode.ToString().ToUpper(), "KomaLab - Stacking algorithm used");

            // DATE-PRO: Quando è avvenuto il calcolo scientifico? (Diverso da DATE scrittura file)
            _metadataService.SetValue(finalHeader, "DATE-PRO", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss"), "KomaLab - Processing timestamp (UTC)");

            // HISTORY: Traccia dell'operazione per l'utente
            _metadataService.AddValue(finalHeader, "HISTORY", $"KomaLab - Stacking: Combined {fileList.Count} frames via {mode} algorithm.", null);

            // 4. Salvataggio
            return await _dataManager.SaveAsTemporaryAsync(finalPixels, finalHeader, "StackResult");
        }
        finally
        {
            foreach (var mat in matsToDispose) mat.Dispose();
        }
    }
}