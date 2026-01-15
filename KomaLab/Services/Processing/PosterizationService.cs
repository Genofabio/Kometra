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
// VERSIONE: Finale (Array Puro + Header Template)
// LOGICA: Invariata.
// ---------------------------------------------------------------------------

public class PosterizationService : IPosterizationService
{
    private readonly IFitsIoService _ioService;
    private readonly IFitsOpenCvConverter _converter;
    private readonly IFitsMetadataService _metadataService;
    private readonly IImageAnalysisService _analysisService; 

    public PosterizationService(
        IFitsIoService ioService, 
        IFitsOpenCvConverter converter,
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
        // Wrapper invariato
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
        // 1. Caricamento Separato (I/O)
        var header = await _ioService.ReadHeaderAsync(inputPath);
        var rawPixels = await _ioService.ReadPixelDataAsync(inputPath);

        if (header == null || rawPixels == null) 
            throw new FileNotFoundException($"File non trovato o illeggibile: {inputPath}");

        // 2. Elaborazione (CPU)
        // Restituisce una tupla (Array Pixels, FitsHeader Header) pronta per il salvataggio
        var resultPackage = await Task.Run(() =>
        {
            double bScale = header.GetValue<double>("BSCALE") ?? 1.0;
            double bZero = header.GetValue<double>("BZERO") ?? 0.0;

            using Mat srcMat = _converter.RawToMat(rawPixels, bScale, bZero);
            using Mat resultMat = new Mat();
            
            // Logica Core
            ComputePosterization(srcMat, resultMat, levels, mode, blackPoint, whitePoint);

            // a. Otteniamo SOLO i pixel grezzi (UInt8 per posterizzazione)
            Array newPixels = _converter.MatToRaw(resultMat, FitsBitDepth.UInt8);

            // b. Costruiamo l'header corretto usando il template originale
            // Questo preserva telescopio, osservatore, ecc. ma aggiorna BITPIX e NAXIS
            var newHeader = _metadataService.CreateHeaderFromTemplate(header, newPixels, FitsBitDepth.UInt8);
            
            // Opzionale: Aggiunta nota storica
            newHeader.Add("HISTORY", null, "Applied Posterization Filter");

            return (Pixels: newPixels, Header: newHeader);
        });

        // 3. Salvataggio (I/O)
        string fileName = Path.GetFileNameWithoutExtension(inputPath);
        string outputPath = Path.Combine(outputFolder, $"{fileName}_Posterized.fits");

        await _ioService.WriteFileAsync(outputPath, resultPackage.Pixels, resultPackage.Header);
        
        return outputPath;
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
            try 
            {
                // 1. Caricamento
                var header = await _ioService.ReadHeaderAsync(path);
                var rawPixels = await _ioService.ReadPixelDataAsync(path);
                
                if (header == null || rawPixels == null) continue;

                // 2. Processing (Scaling + Math + Header Gen)
                var resultPackage = await Task.Run(() =>
                {
                    double bScale = header.GetValue<double>("BSCALE") ?? 1.0;
                    double bZero = header.GetValue<double>("BZERO") ?? 0.0;

                    using Mat srcMat = _converter.RawToMat(rawPixels, bScale, bZero);

                    // Auto-Stretch su ogni singola immagine
                    var profile = _analysisService.CalculateAutoStretchProfile(srcMat);

                    double finalBlack = profile.BlackAdu + blackOffset;
                    double finalWhite = profile.WhiteAdu + whiteOffset;

                    using Mat resultMat = new Mat();
                    ComputePosterization(srcMat, resultMat, levels, mode, finalBlack, finalWhite);

                    // Generazione Output
                    Array newPixels = _converter.MatToRaw(resultMat, FitsBitDepth.UInt8);
                    var newHeader = _metadataService.CreateHeaderFromTemplate(header, newPixels, FitsBitDepth.UInt8);

                    return (Pixels: newPixels, Header: newHeader);
                });

                // 3. Salvataggio
                string fileName = Path.GetFileNameWithoutExtension(path);
                string outputPath = Path.Combine(outputFolder, $"{fileName}_Posterized.fits");
                
                await _ioService.WriteFileAsync(outputPath, resultPackage.Pixels, resultPackage.Header);
                results.Add(outputPath);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Errore batch file {path}: {ex.Message}");
            }
        }

        return results;
    }

    // --- 4. Core Algorithm (Pure Math) ---

    // LOGICA ASSOLUTAMENTE IDENTICA ALL'ORIGINALE
    public static void ComputePosterization(Mat src, Mat dst, int levels, VisualizationMode mode, double bp, double wp)
    {
        double range = wp - bp;
        if (Math.Abs(range) < 1e-9) range = 1e-5;
        
        using Mat temp = new Mat();
        double scale = 1.0 / range;
        double offset = -bp * scale;

        // 1. Normalizzazione [0..1]
        src.ConvertTo(temp, MatType.CV_32FC1, scale, offset);
        Cv2.Max(temp, 0.0, temp);
        Cv2.Min(temp, 1.0, temp);

        // 2. Curva di Trasferimento
        if (mode == VisualizationMode.SquareRoot)
            Cv2.Sqrt(temp, temp);
        else if (mode == VisualizationMode.Logarithmic)
        {
            Cv2.Add(temp, 1.0, temp);
            Cv2.Log(temp, temp);
            Cv2.Multiply(temp, 1.442695, temp); 
        }

        // 3. Quantizzazione (Posterizzazione)
        Cv2.Multiply(temp, (double)levels - 0.001, temp);
        
        using Mat intTemp = new Mat();
        temp.ConvertTo(intTemp, MatType.CV_32SC1);
        intTemp.ConvertTo(temp, MatType.CV_32FC1); 
        
        double divScale = levels > 1 ? 1.0 / (levels - 1) : 1.0;
        Cv2.Multiply(temp, divScale, temp);

        // 4. Output
        temp.ConvertTo(dst, MatType.CV_8UC1, 255.0);
    }
}