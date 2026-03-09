using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Kometra.Models.Fits;
using Kometra.Models.Fits.Structure;
using Kometra.Models.Processing.Arithmetic;
using Kometra.Services.Fits;
using Kometra.Services.Fits.Conversion;
using Kometra.Services.Fits.Metadata;
using Kometra.Services.Processing.Engines;
using OpenCvSharp;

namespace Kometra.Services.Processing.Coordinators;

public class ArithmeticCoordinator : IArithmeticCoordinator
{
    private readonly IFitsDataManager _dataManager;
    private readonly IFitsMetadataService _metadataService;
    private readonly IFitsOpenCvConverter _converter;
    private readonly IArithmeticEngine _engine;

    public ArithmeticCoordinator(
        IFitsDataManager dataManager,
        IFitsMetadataService metadataService,
        IFitsOpenCvConverter converter,
        IArithmeticEngine engine)
    {
        _dataManager = dataManager ?? throw new ArgumentNullException(nameof(dataManager));
        _metadataService = metadataService ?? throw new ArgumentNullException(nameof(metadataService));
        _converter = converter ?? throw new ArgumentNullException(nameof(converter));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
    }

    public async Task<List<string>> ProcessAsync(
        List<FitsFileReference> listA, 
        List<FitsFileReference> listB, 
        ArithmeticOperation op)
    {
        var results = new List<string>();

        // Determiniamo il modo di iterazione:
        // Se B ha 1 solo elemento, viene riutilizzato per ogni elemento di A (Broadcasting).
        // Se entrambi hanno N elementi, vengono accoppiati 1:1.
        bool isBSequence = listB.Count > 1;
        int count = listA.Count;

        for (int i = 0; i < count; i++)
        {
            var fileA = listA[i];
            var fileB = isBSequence ? listB[i] : listB[0];

            using var matA = await LoadMatAsync(fileA);
            using var matB = await LoadMatAsync(fileB);

            // L'Engine esegue il calcolo e restituisce una Mat (Float o Double)
            using var resultMat = _engine.Execute(matA, matB, op);

            // Scelta automatica della profondità FITS basata sulla Mat risultante
            FitsBitDepth outputDepth = (resultMat.Depth() == MatType.CV_64F) 
                ? FitsBitDepth.Double 
                : FitsBitDepth.Float;

            // Conversione in RAW Array tramite il Converter
            var raw = _converter.MatToRaw(resultMat, outputDepth);
            
            // Creazione Header basato sul template A
            var baseHeader = fileA.ModifiedHeader ?? (await _dataManager.GetHeaderOnlyAsync(fileA.FilePath));
            var newHeader = _metadataService.CreateHeaderFromTemplate(baseHeader, raw, outputDepth);

            // 1. RESET DEI PARAMETRI DI SCALA
            // Poiché salviamo in Float/Double, non vogliamo che i valori originali di BSCALE/BZERO 
            // (tipici degli Int16) vengano riapplicati erroneamente in lettura.
            _metadataService.SetValue(newHeader, "BSCALE", 1.0);
            _metadataService.SetValue(newHeader, "BZERO", 0.0);

            // 2. TRACCIABILITÀ TRAMITE HISTORY (Standard FITS)
            string fileNameB = System.IO.Path.GetFileName(fileB.FilePath);
            string opName = op.ToString().ToUpper();
            
            _metadataService.AddValue(newHeader, "HISTORY", $"Arithmetic operation: {opName}");
            _metadataService.AddValue(newHeader, "HISTORY", $"Operand A: {System.IO.Path.GetFileName(fileA.FilePath)}");
            _metadataService.AddValue(newHeader, "HISTORY", $"Operand B: {fileNameB}");
            _metadataService.AddValue(newHeader, "HISTORY", $"Processed at: {DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss} UTC");

            // 3. CALCOLO DATAMAX/DATAMIN (Standard)
            resultMat.MinMaxLoc(out double min, out double max);
            _metadataService.SetValue(newHeader, "DATAMIN", min);
            _metadataService.SetValue(newHeader, "DATAMAX", max);

            // Salvataggio temporaneo
            var resultRef = await _dataManager.SaveAsTemporaryAsync(raw, newHeader, $"Arith_{op}_{i}");
            results.Add(resultRef.FilePath);
        }

        return results;
    }
    
    public bool CanProcess(List<FitsFileReference> listA, List<FitsFileReference> listB)
    {
        if (listA == null || listB == null || listA.Count == 0 || listB.Count == 0) 
            return false;

        int countA = listA.Count;
        int countB = listB.Count;

        // 1. Singolo con Singolo (1:1)
        if (countA == 1 && countB == 1) return true;

        // 2. Sequenza A con Singolo B (N:1)
        if (countA > 1 && countB == 1) return true;

        // 3. Sequenza A con Sequenza B (N:N) -> Solo se hanno lo stesso numero di frame
        if (countA > 1 && countB > 1 && countA == countB) return true;

        return false;
    }

    private async Task<Mat> LoadMatAsync(FitsFileReference file)
    {
        var data = await _dataManager.GetDataAsync(file.FilePath);
        var hdu = data.FirstImageHdu ?? data.PrimaryHdu;
        if (hdu == null) throw new InvalidOperationException("File does not contain valid image data.");

        var h = file.ModifiedHeader ?? hdu.Header;
        double bScale = _metadataService.GetDoubleValue(h, "BSCALE", 1.0);
        double bZero = _metadataService.GetDoubleValue(h, "BZERO", 0.0);
        
        return _converter.RawToMat(hdu.PixelData, bScale, bZero, null);
    }
}