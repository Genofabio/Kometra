using System;
using System.IO;
using System.Threading.Tasks;
using KomaLab.Models;
using KomaLab.Services.Data;
using nom.tam.fits;
using OpenCvSharp;

namespace KomaLab.Services.Imaging;

public class PosterizationService : IPosterizationService
{
    private readonly IFitsService _fitsService;
    private readonly IFitsDataConverter _converter;

    public PosterizationService(IFitsService fitsService, IFitsDataConverter converter)
    {
        _fitsService = fitsService;
        _converter = converter;
    }

    public async Task<string> PosterizeAndSaveAsync(
        string inputPath,
        string outputFolder,
        int levels,
        VisualizationMode mode,
        double blackPoint,
        double whitePoint)
    {
        return await Task.Run(async () =>
        {
            // 1. Caricamento
            var fitsData = await _fitsService.LoadFitsFromFileAsync(inputPath);
            if (fitsData == null) throw new FileNotFoundException($"File non trovato: {inputPath}");

            // 2. Conversione in Matrice OpenCV
            using Mat srcMat = _converter.RawToMat(fitsData);
            using Mat resultMat = new Mat();

            // 3. Calcolo
            ComputePosterization(srcMat, resultMat, levels, mode, blackPoint, whitePoint);

            // 4. Conversione Output: Mat -> Jagged Array 2D (byte[][])
            // CRUCIALE: CSharpFITS ha bisogno di un array di array (byte[][]) per capire 
            // che deve creare un'immagine 2D (NAXIS=2).
            // Se passiamo un byte[] piatto, crea un FITS 1D che poi fallisce il caricamento.
            
            int w = resultMat.Width;
            int h = resultMat.Height;
            byte[][] jaggedPixels = new byte[h][];

            for (int y = 0; y < h; y++)
            {
                jaggedPixels[y] = new byte[w];
                // Copia efficiente riga per riga da OpenCV (Unmanaged) a C# (Managed)
                var srcPtr = resultMat.Ptr(y);
                System.Runtime.InteropServices.Marshal.Copy(srcPtr, jaggedPixels[y], 0, w);
            }

            // 5. Header
            // Non serve aggiungere BITPIX o NAXIS qui: FitsService.SaveFitsFileAsync li genera
            // automaticamente basandosi sul tipo di dati (jaggedPixels è byte[][] -> BITPIX=8, NAXIS=2).
            var newHeader = CloneHeader(fitsData.FitsHeader);
            newHeader.AddCard(new HeaderCard("HISTORY", $"Posterized: {levels} levels, Mode: {mode}", null));

            // 6. Salvataggio
            string fileName = Path.GetFileNameWithoutExtension(inputPath);
            string outputPath = Path.Combine(outputFolder, $"{fileName}_Posterized_{levels}L.fits");

            var resultData = new FitsImageData
            {
                RawData = jaggedPixels, // Passiamo la struttura 2D!
                FitsHeader = newHeader,
                Width = w,
                Height = h
            };

            await _fitsService.SaveFitsFileAsync(resultData, outputPath);

            return outputPath;
        });
    }

    public static void ComputePosterization(Mat src, Mat dst, int levels, VisualizationMode mode, double bp, double wp)
    {
        double range = wp - bp;
        if (Math.Abs(range) < 1e-9) range = 1e-5;
        
        using Mat temp = new Mat();
        double scale = 1.0 / range;
        double offset = -bp * scale;

        // Normalizza
        src.ConvertTo(temp, MatType.CV_32FC1, scale, offset);
        Cv2.Max(temp, 0.0, temp);
        Cv2.Min(temp, 1.0, temp);

        // Curva
        if (mode == VisualizationMode.SquareRoot) Cv2.Sqrt(temp, temp);
        else if (mode == VisualizationMode.Logarithmic)
        {
            Cv2.Add(temp, 1.0, temp);
            Cv2.Log(temp, temp);
            Cv2.Multiply(temp, 1.442695, temp);
        }

        // Quantizzazione
        Cv2.Multiply(temp, (double)levels - 0.001, temp);
        using Mat intTemp = new Mat();
        temp.ConvertTo(intTemp, MatType.CV_32SC1); // Floor
        intTemp.ConvertTo(temp, MatType.CV_32FC1);
        
        double divScale = levels > 1 ? 1.0 / (levels - 1) : 1.0;
        Cv2.Multiply(temp, divScale, temp);

        // Output 8-bit
        temp.ConvertTo(dst, MatType.CV_8UC1, 255.0, 0);
    }

    private Header CloneHeader(Header original)
    {
        var newHeader = new Header();
        var cursor = original.GetCursor();

        while (cursor.MoveNext())
        {
            var card = cursor.Current as HeaderCard;
            if (card == null) continue;

            string key = card.Key?.ToUpper() ?? "";

            // --- BLACKLIST ---
            // Queste keyword vengono gestite automaticamente dalla libreria quando salviamo l'array
            // o non sono più valide per il nuovo formato a 8-bit.
            if (key == "SIMPLE" || 
                key == "BITPIX" || 
                key == "NAXIS" || 
                key == "NAXIS1" || 
                key == "NAXIS2" || 
                key == "PCOUNT" || 
                key == "GCOUNT" || 
                key == "EXTEND" ||
                key == "BSCALE" || // Importante rimuoverlo: siamo 8-bit puri ora
                key == "BZERO" ||  // Importante rimuoverlo
                key == "CHECKSUM" || 
                key == "DATASUM")
            {
                continue;
            }

            // Copiamo tutto il resto (TELESCOP, OBJECT, DATE-OBS, WCS coords, HISTORY, COMMENT, ecc.)
            try 
            {
                // Se è un commento o history (chiave nulla o specifica), usiamo un costruttore diverso se necessario
                // Ma in CSharpFITS solitamente clonare la card è sicuro.
                newHeader.AddCard(new HeaderCard(card.Key, card.Value, card.Comment));
            }
            catch 
            {
                // Ignora card corrotte, ma non fermare il ciclo
            }
        }
        return newHeader;
    }
}