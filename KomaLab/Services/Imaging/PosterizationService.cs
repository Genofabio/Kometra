using System;
using System.IO;
using System.Threading.Tasks;
using OpenCvSharp;
using KomaLab.Models.Fits;        // Per FitsBitDepth
using KomaLab.Models.Visualization;
using KomaLab.Services.Data;      // Per IoService, Converter, MetadataService
using nom.tam.fits;

namespace KomaLab.Services.Imaging;

// ---------------------------------------------------------------------------
// FILE: PosterizationService.cs
// RUOLO: Servizio Effetti (Image Processing)
// DESCRIZIONE:
// Applica l'effetto di Posterizzazione.
// ARCHITETTURA: Pura Business Logic. Non gestisce puntatori, non gestisce I/O raw.
// ---------------------------------------------------------------------------

public class PosterizationService : IPosterizationService
{
    private readonly IFitsIoService _ioService;           // I/O Resiliente
    private readonly IFitsImageDataConverter _converter;  // Marshalling & Polimorfismo
    private readonly IFitsMetadataService _metadataService; // Gestione Header

    public PosterizationService(
        IFitsIoService ioService, 
        IFitsImageDataConverter converter,
        IFitsMetadataService metadataService)
    {
        _ioService = ioService ?? throw new ArgumentNullException(nameof(ioService));
        _converter = converter ?? throw new ArgumentNullException(nameof(converter));
        _metadataService = metadataService ?? throw new ArgumentNullException(nameof(metadataService));
    }

    public async Task<string> PosterizeAndSaveAsync(
        string inputPath,
        string outputFolder,
        int levels,
        VisualizationMode mode,
        double blackPoint,
        double whitePoint)
    {
        // 1. Caricamento (Delegato a IO Service)
        var fitsData = await _ioService.LoadAsync(inputPath);
        if (fitsData == null) throw new FileNotFoundException($"File non trovato: {inputPath}");

        return await Task.Run(async () =>
        {
            // 2. Preparazione Matrice (Delegata a Converter)
            using Mat srcMat = _converter.RawToMat(fitsData);
            
            // 3. Elaborazione Matematica (Business Logic pura)
            using Mat resultMat = new Mat();
            ComputePosterization(srcMat, resultMat, levels, mode, blackPoint, whitePoint);

            // 4. Conversione Output ESPLICITA a 8-BIT
            // Qui sfruttiamo il Converter Polimorfico. Gli diciamo:
            // "Prendi questa matrice e dammi una struttura FITS pronta per essere salvata come UInt8".
            // Il converter gestirà internamente il Marshal.Copy e la creazione di byte[][].
            var resultData = _converter.MatToFitsData(resultMat, FitsBitDepth.UInt8);

            // 5. Gestione Metadati (Delegata a Metadata Service)
            // Copia i dati astronomici (Telescopio, Data, RA/DEC) filtrando via BSCALE/BZERO vecchi.
            _metadataService.TransferMetadata(fitsData.FitsHeader, resultData.FitsHeader);
            
            // Aggiungiamo solo le note specifiche di questo processo
            resultData.FitsHeader.AddCard(new HeaderCard("HISTORY", $"KomaLab Posterization: {levels} levels", null));
            resultData.FitsHeader.AddCard(new HeaderCard("HISTORY", $"Mode: {mode}", null));

            // 6. Salvataggio
            string fileName = Path.GetFileNameWithoutExtension(inputPath);
            if (!Directory.Exists(outputFolder)) Directory.CreateDirectory(outputFolder);
            
            string outputPath = Path.Combine(outputFolder, $"{fileName}_Posterized.fits");

            await _ioService.SaveAsync(resultData, outputPath);

            return outputPath;
        });
    }

    /// <summary>
    /// Logica matematica pure OpenCV.
    /// Accetta 64-bit in ingresso, produce 8-bit in uscita.
    /// </summary>
    private static void ComputePosterization(Mat src, Mat dst, int levels, VisualizationMode mode, double bp, double wp)
    {
        double range = wp - bp;
        if (Math.Abs(range) < 1e-9) range = 1e-5;
        
        using Mat temp = new Mat();
        double scale = 1.0 / range;
        double offset = -bp * scale;

        // Normalizzazione
        src.ConvertTo(temp, MatType.CV_32FC1, scale, offset);
        Cv2.Max(temp, 0.0, temp);
        Cv2.Min(temp, 1.0, temp);

        // Stretch Non Lineare
        if (mode == VisualizationMode.SquareRoot)
        {
            Cv2.Sqrt(temp, temp);
        }
        else if (mode == VisualizationMode.Logarithmic)
        {
            Cv2.Add(temp, 1.0, temp);
            Cv2.Log(temp, temp);
            Cv2.Multiply(temp, 1.442695, temp);
        }

        // Quantizzazione (Posterizzazione)
        Cv2.Multiply(temp, levels - 0.001, temp);
        
        using Mat intTemp = new Mat();
        temp.ConvertTo(intTemp, MatType.CV_32SC1); // Tronca a interi (es. 0, 1, 2, 3)
        intTemp.ConvertTo(temp, MatType.CV_32FC1); // Torna float
        
        // Riscala a 0..1
        double divScale = levels > 1 ? 1.0 / (levels - 1) : 1.0;
        Cv2.Multiply(temp, divScale, temp);

        // Conversione finale a 8-bit [0..255]
        // Fondamentale: resultMat (dst) diventa CV_8UC1 qui.
        temp.ConvertTo(dst, MatType.CV_8UC1, 255.0);
    }
}