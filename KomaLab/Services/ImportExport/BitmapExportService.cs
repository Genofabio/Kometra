using System;
using System.IO;
using System.Linq;
using KomaLab.Models.Export;
using KomaLab.Models.Fits.Structure;
using KomaLab.Models.Visualization;
using KomaLab.Services.Fits.Conversion;
using KomaLab.Services.Processing.Rendering;
using OpenCvSharp;
// Fondamentale per FirstOrDefault

namespace KomaLab.Services.ImportExport; // Allineato alla tua cartella IO

public interface IBitmapExportService
{
    void ExportBitmap(Array pixelData, FitsHeader header, string outputPath, ExportFormat format, int quality, AbsoluteContrastProfile? stretchProfile = null);
}

public class BitmapExportService : IBitmapExportService
{
    private readonly IFitsOpenCvConverter _converter;
    private readonly IImagePresentationService _presentationService;

    public BitmapExportService(
        IFitsOpenCvConverter converter,
        IImagePresentationService presentationService)
    {
        _converter = converter ?? throw new ArgumentNullException(nameof(converter));
        _presentationService = presentationService ?? throw new ArgumentNullException(nameof(presentationService));
    }

    public void ExportBitmap(
        Array pixelData, 
        FitsHeader header, 
        string outputPath, 
        ExportFormat format, 
        int quality, 
        AbsoluteContrastProfile? stretchProfile = null)
    {
        // 1. Recupera BSCALE/BZERO
        double bScale = GetHeaderDouble(header, "BSCALE", 1.0);
        double bZero = GetHeaderDouble(header, "BZERO", 0.0);

        // 2. Converti in Mat (Float)
        using Mat srcMat = _converter.RawToMat(pixelData, bScale, bZero, FitsBitDepth.Float);

        // 3. Determina lo Stretch
        double black, white;
        
        if (stretchProfile != null)
        {
            black = stretchProfile.BlackAdu;
            white = stretchProfile.WhiteAdu;
        }
        else
        {
            // AutoStretch se non fornito
            var autoProfile = _presentationService.GetInitialProfile(srcMat);
            black = autoProfile.BlackAdu;
            white = autoProfile.WhiteAdu;
        }

        // 4. Renderizza a 8-bit
        using Mat dst8Bit = new Mat();
        _presentationService.RenderTo8Bit(srcMat, dst8Bit, black, white, VisualizationMode.Linear);

        // 5. Parametri OpenCV
        int[] prms = null;
        if (format == ExportFormat.JPEG)
        {
            prms = new[] { (int)ImwriteFlags.JpegQuality, quality };
        }
        else if (format == ExportFormat.PNG)
        {
            prms = new[] { (int)ImwriteFlags.PngCompression, 3 }; 
        }

        // 6. Scrittura
        if (!Cv2.ImWrite(outputPath, dst8Bit, prms))
        {
            throw new IOException($"OpenCV failed to save image to {outputPath}");
        }
    }

    private double GetHeaderDouble(FitsHeader h, string key, double def)
    {
        var card = h.Cards.FirstOrDefault(c => c.Key == key);
        if (card != null && double.TryParse(card.Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double val))
            return val;
        return def;
    }
}