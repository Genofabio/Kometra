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

            // 3. Calcolo (Stessa logica usata nell'anteprima)
            ComputePosterization(srcMat, resultMat, levels, mode, blackPoint, whitePoint);

            // 4. Conversione Output (Mat -> Byte Array per FITS)
            int w = resultMat.Width;
            int h = resultMat.Height;
            byte[] pixelBytes = new byte[w * h];
            System.Runtime.InteropServices.Marshal.Copy(resultMat.Data, pixelBytes, 0, pixelBytes.Length);

            // 5. Gestione Header
            var newHeader = CloneHeader(fitsData.FitsHeader);
            newHeader.AddCard(new HeaderCard("BITPIX", 8, "8-bit unsigned integer"));
            newHeader.AddCard(new HeaderCard("HISTORY", $"Posterized: {levels} levels, Mode: {mode}", null));

            // 6. Salvataggio
            string fileName = Path.GetFileNameWithoutExtension(inputPath);
            string outputPath = Path.Combine(outputFolder, $"{fileName}_Posterized_{levels}L.fits");

            var resultData = new FitsImageData
            {
                RawData = pixelBytes,
                FitsHeader = newHeader,
                Width = w,
                Height = h
            };

            await _fitsService.SaveFitsFileAsync(resultData, outputPath);

            return outputPath;
        });
    }

    // Logica matematica condivisa
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
        var cursor = original.GetCursor();
        var newHeader = new Header();
        while (cursor.MoveNext())
        {
            if (cursor.Current is HeaderCard c)
            {
                string k = c.Key.ToUpper();
                if (k != "BITPIX" && k != "BSCALE" && k != "BZERO" && k != "SIMPLE" && k != "GCOUNT" && k != "PCOUNT")
                    newHeader.AddCard(new HeaderCard(c.Key, c.Value, c.Comment));
            }
        }
        return newHeader;
    }
}