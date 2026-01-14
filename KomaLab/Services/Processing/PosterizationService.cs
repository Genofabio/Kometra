using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using KomaLab.Models.Fits; 
using KomaLab.Models.Visualization;
using KomaLab.Services.Fits;
using OpenCvSharp;

namespace KomaLab.Services.Processing;

// ---------------------------------------------------------------------------
// FILE: PosterizationService.cs
// RUOLO: Servizio Effetti (Image Processing)
// VERSIONE: Aggiornata (No nom.tam.fits, No Header Modifiers)
// ---------------------------------------------------------------------------

public class PosterizationService : IPosterizationService
{
    private readonly IFitsIoService _ioService;
    private readonly IFitsImageDataConverter _converter;
    private readonly IFitsMetadataService _metadataService;
    private readonly IImageAnalysisService _analysisService; 

    public PosterizationService(
        IFitsIoService ioService, 
        IFitsImageDataConverter converter,
        IFitsMetadataService metadataService,
        IImageAnalysisService analysisService)
    {
        _ioService = ioService ?? throw new ArgumentNullException(nameof(ioService));
        _converter = converter ?? throw new ArgumentNullException(nameof(converter));
        _metadataService = metadataService ?? throw new ArgumentNullException(nameof(metadataService));
        _analysisService = analysisService ?? throw new ArgumentNullException(nameof(analysisService));
    }

    // --- 1. Calcolo in Memoria (Per ViewModel / Anteprima) ---

    public void ComputePosterizationOnMat(Mat src, Mat dst, int levels, VisualizationMode mode, double bp, double wp)
    {
        // Wrapper per il metodo statico interno
        ComputePosterization(src, dst, levels, mode, bp, wp);
    }

    // --- 2. Elaborazione Singola su Disco ---

    public async Task<string> PosterizeAndSaveAsync(
        string inputPath,
        string outputFolder,
        int levels,
        VisualizationMode mode,
        double blackPoint,
        double whitePoint)
    {
        var fitsData = await _ioService.LoadAsync(inputPath);
        if (fitsData == null) throw new FileNotFoundException($"File non trovato: {inputPath}");

        return await Task.Run(async () =>
        {
            using Mat srcMat = _converter.RawToMat(fitsData);
            using Mat resultMat = new Mat();
            
            // Riutilizzo logica centrale
            ComputePosterization(srcMat, resultMat, levels, mode, blackPoint, whitePoint);

            // CORREZIONE: Usiamo l'header originale come template per la deep copy dei metadati.
            // Impostiamo l'output a UInt8 (8-bit) perché la posterizzazione produce valori 0-255.
            var resultData = _converter.MatToFitsData(resultMat, FitsBitDepth.UInt8, fitsData.FitsHeader);
            
            // RIMOSSO: Aggiunta manuale di HISTORY (come richiesto, header intatto)

            string fileName = Path.GetFileNameWithoutExtension(inputPath);
            string outputPath = Path.Combine(outputFolder, $"{fileName}_Posterized.fits");

            await _ioService.SaveAsync(resultData, outputPath);
            return outputPath;
        });
    }

    // --- 3. Elaborazione Batch Adattiva ---

    public async Task<List<string>> PosterizeBatchWithOffsetsAsync(
        List<string> inputPaths,
        string outputFolder,
        int levels,
        VisualizationMode mode,
        double blackOffset,
        double whiteOffset)
    {
        var results = new List<string>();

        foreach (var path in inputPaths)
        {
            var fitsData = await _ioService.LoadAsync(path);
            if (fitsData == null) continue;

            using Mat srcMat = _converter.RawToMat(fitsData);

            // Calcolo profilo automatico
            var profile = await Task.Run(() => _analysisService.CalculateAutoStretchProfile(srcMat));

            // Applichiamo l'offset dinamico
            double finalBlack = profile.BlackAdu + blackOffset;
            double finalWhite = profile.WhiteAdu + whiteOffset;

            using Mat resultMat = new Mat();
            ComputePosterization(srcMat, resultMat, levels, mode, finalBlack, finalWhite);

            // CORREZIONE: Uso del template header per copiare i metadati
            var resultData = _converter.MatToFitsData(resultMat, FitsBitDepth.UInt8, fitsData.FitsHeader);
            
            string fileName = Path.GetFileNameWithoutExtension(path);
            string outputPath = Path.Combine(outputFolder, $"{fileName}_Posterized.fits");
            await _ioService.SaveAsync(resultData, outputPath);

            results.Add(outputPath);
        }

        return results;
    }

    // --- 4. Core Algorithm (Pure Math) ---

    // Metodo statico privato (o pubblico se serve come utility helper senza stato)
    // Contiene la logica OpenCV pura per non duplicare codice.
    public static void ComputePosterization(Mat src, Mat dst, int levels, VisualizationMode mode, double bp, double wp)
    {
        double range = wp - bp;
        if (Math.Abs(range) < 1e-9) range = 1e-5;
        
        using Mat temp = new Mat();
        double scale = 1.0 / range;
        double offset = -bp * scale;

        // 1. Normalizzazione [0..1] basata su Black/White Point
        src.ConvertTo(temp, MatType.CV_32FC1, scale, offset);
        Cv2.Max(temp, 0.0, temp);
        Cv2.Min(temp, 1.0, temp);

        // 2. Applicazione Curva di Trasferimento
        if (mode == VisualizationMode.SquareRoot)
            Cv2.Sqrt(temp, temp);
        else if (mode == VisualizationMode.Logarithmic)
        {
            // log(1 + x) / log(2) per mappare 0..1 -> 0..1
            Cv2.Add(temp, 1.0, temp);
            Cv2.Log(temp, temp);
            Cv2.Multiply(temp, 1.442695, temp); // 1.442... = 1/ln(2)
        }

        // 3. Quantizzazione (Posterizzazione)
        // Scaliamo a [0 .. Levels]
        Cv2.Multiply(temp, (double)levels - 0.001, temp);
        
        using Mat intTemp = new Mat();
        // Troncamento intero (Quantizzazione)
        temp.ConvertTo(intTemp, MatType.CV_32SC1);
        // Ritorno a Float
        intTemp.ConvertTo(temp, MatType.CV_32FC1); 
        
        // Riscaliamo indietro a [0..1]
        double divScale = levels > 1 ? 1.0 / (levels - 1) : 1.0;
        Cv2.Multiply(temp, divScale, temp);

        // 4. Output a 8 bit [0..255] per visualizzazione/salvataggio
        temp.ConvertTo(dst, MatType.CV_8UC1, 255.0);
    }
}