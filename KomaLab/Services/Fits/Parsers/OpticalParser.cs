using System;
using KomaLab.Models.Fits;
using KomaLab.Models.Fits.Structure;
using KomaLab.Services.Fits.Metadata;

namespace KomaLab.Services.Fits.Parsers;

public static class OpticalParser
{
    private static readonly string[] FocalKeys = { "FOCALLEN", "FOCAL", "HIERARCH CAHA TEL FOCU LEN" };
    private static readonly string[] PixelKeys = { "XPIXSZ", "PIXSIZE", "CCDPSIZ", "INSTRPIX", "HIERARCH CAHA DET CCD1 PSZX" };

    public static double? ParseFocalLength(FitsHeader header, IFitsMetadataService metadata)
    {
        foreach (var key in FocalKeys)
        {
            // Usiamo il servizio per estrarre il valore in modo sicuro
            double val = metadata.GetDoubleValue(header, key, -1);
            if (val <= 0) continue;

            // Logica specifica CAHA (m -> mm)
            if (key.Contains("CAHA") && key.Contains("LEN"))
                return val * 1000.0;

            return val;
        }
        return null;
    }

    public static double? ParsePixelSize(FitsHeader header, IFitsMetadataService metadata)
    {
        foreach (var key in PixelKeys)
        {
            double val = metadata.GetDoubleValue(header, key, -1);
            if (val > 0) return val;
        }
        return null;
    }
}