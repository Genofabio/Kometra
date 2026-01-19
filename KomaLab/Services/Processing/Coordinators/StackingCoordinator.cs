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
                double bScale = _metadataService.GetDoubleValue(data.Header, "BSCALE", 1.0);
                double bZero = _metadataService.GetDoubleValue(data.Header, "BZERO", 0.0);
                var mat = _converter.RawToMat(data.PixelData, bScale, bZero);
                matsToDispose.Add(mat);
            }

            // 2. Calcolo Stacking
            using var resultMat = await _stackingEngine.ComputeStackAsync(matsToDispose, mode);
            var finalPixels = _converter.MatToRaw(resultMat, FitsBitDepth.Double);

            // 3. Preparazione Header
            var firstFrameData = await _dataManager.GetDataAsync(fileList[0].FilePath);
            var finalHeader = _metadataService.CreateHeaderFromTemplate(
                firstFrameData.Header, 
                finalPixels, 
                FitsBitDepth.Double
            );

            // --- CHIAVI UNICHE (Usa SetValue per evitare duplicati se il file viene ri-elaborato) ---
            
            // Identità del software
            _metadataService.SetValue(finalHeader, "CREATOR", "KomaLab", "Software that created this file");

            // Dati scientifici dello stack
            _metadataService.SetValue(finalHeader, "NCOMBINE", fileList.Count, "KomaLab - Number of combined frames");
            _metadataService.SetValue(finalHeader, "STACKMET", mode.ToString().ToUpper(), "KomaLab - Stacking algorithm used");

            // Data dell'ultima elaborazione
            _metadataService.SetValue(finalHeader, "DATE-PRO", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss"), "KomaLab - Last processing UTC date");

            // --- CHIAVI ADDITIVE (Usa AddValue per mantenere la cronologia) ---
            
            // Nella HISTORY il testo va nel parametro 'value'. Il parametro 'comment' va lasciato null.
            _metadataService.AddValue(finalHeader, "HISTORY", $"KomaLab - Stacking: {fileList.Count} frames combined using {mode} algorithm.", null);

            // 4. Salvataggio
            return await _dataManager.SaveAsTemporaryAsync(finalPixels, finalHeader, "StackResult");
        }
        finally
        {
            foreach (var mat in matsToDispose) mat.Dispose();
        }
    }
}