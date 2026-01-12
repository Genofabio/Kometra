using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using KomaLab.Models.Fits;
using KomaLab.Models.Visualization;
using KomaLab.Services.Fits;
using nom.tam.fits;
using OpenCvSharp;
// Per FitsBitDepth
// Per IoService, Converter, MetadataService

namespace KomaLab.Services.Processing;

// ---------------------------------------------------------------------------
// FILE: PosterizationService.cs
// RUOLO: Servizio Effetti (Image Processing)
// DESCRIZIONE:
// Applica l'effetto di Posterizzazione.
// ARCHITETTURA: Pura Business Logic. Non gestisce puntatori, non gestisce I/O raw.
// ---------------------------------------------------------------------------

public class PosterizationService : IPosterizationService
{
    private readonly IFitsIoService _ioService;
    private readonly IFitsImageDataConverter _converter;
    private readonly IFitsMetadataService _metadataService;
    private readonly IImageAnalysisService _analysisService; // Nuova dipendenza per il batch

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
            
            ComputePosterization(srcMat, resultMat, levels, mode, blackPoint, whitePoint);

            var resultData = _converter.MatToFitsData(resultMat, FitsBitDepth.UInt8);
            _metadataService.TransferMetadata(fitsData.FitsHeader, resultData.FitsHeader);
            
            resultData.FitsHeader.AddCard(new HeaderCard("HISTORY", $"KomaLab Posterization: {levels} levels", null));
            resultData.FitsHeader.AddCard(new HeaderCard("HISTORY", $"Stretch Mode: {mode}", null));

            string fileName = Path.GetFileNameWithoutExtension(inputPath);
            string outputPath = Path.Combine(outputFolder, $"{fileName}_Posterized.fits");

            await _ioService.SaveAsync(resultData, outputPath);
            return outputPath;
        });
    }

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
            // Per ogni immagine nel batch:
            // 1. Carichiamo l'immagine
            var fitsData = await _ioService.LoadAsync(path);
            if (fitsData == null) continue;

            using Mat srcMat = _converter.RawToMat(fitsData);

            // 2. Calcoliamo lo stretch automatico locale (Business Logic spostata dal VM)
            var (autoB, autoW) = await Task.Run(() => _analysisService.CalculateAutoStretchLevels(srcMat));

            // 3. Applichiamo l'offset fornito dall'utente nel ViewModel
            double finalBlack = autoB + blackOffset;
            double finalWhite = autoW + whiteOffset;

            // 4. Elaboriamo e salviamo
            // Riutilizziamo la logica interna per evitare duplicazioni
            using Mat resultMat = new Mat();
            ComputePosterization(srcMat, resultMat, levels, mode, finalBlack, finalWhite);

            var resultData = _converter.MatToFitsData(resultMat, FitsBitDepth.UInt8);
            _metadataService.TransferMetadata(fitsData.FitsHeader, resultData.FitsHeader);
            
            string fileName = Path.GetFileNameWithoutExtension(path);
            string outputPath = Path.Combine(outputFolder, $"{fileName}_Posterized.fits");
            await _ioService.SaveAsync(resultData, outputPath);

            results.Add(outputPath);
        }

        return results;
    }

    public static void ComputePosterization(Mat src, Mat dst, int levels, VisualizationMode mode, double bp, double wp)
    {
        double range = wp - bp;
        if (Math.Abs(range) < 1e-9) range = 1e-5;
        
        using Mat temp = new Mat();
        double scale = 1.0 / range;
        double offset = -bp * scale;

        src.ConvertTo(temp, MatType.CV_32FC1, scale, offset);
        Cv2.Max(temp, 0.0, temp);
        Cv2.Min(temp, 1.0, temp);

        if (mode == VisualizationMode.SquareRoot)
            Cv2.Sqrt(temp, temp);
        else if (mode == VisualizationMode.Logarithmic)
        {
            Cv2.Add(temp, 1.0, temp);
            Cv2.Log(temp, temp);
            Cv2.Multiply(temp, 1.442695, temp);
        }

        Cv2.Multiply(temp, (double)levels - 0.001, temp);
        
        using Mat intTemp = new Mat();
        temp.ConvertTo(intTemp, MatType.CV_32SC1);
        intTemp.ConvertTo(temp, MatType.CV_32FC1); 
        
        double divScale = levels > 1 ? 1.0 / (levels - 1) : 1.0;
        Cv2.Multiply(temp, divScale, temp);

        temp.ConvertTo(dst, MatType.CV_8UC1, 255.0);
    }
}