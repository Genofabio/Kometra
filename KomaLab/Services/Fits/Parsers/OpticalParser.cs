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
        // Proviamo la chiave HIERARCH specifica del file 2
        double cahaFocal = metadata.GetDoubleValue(header, "HIERARCH CAHA TEL FOCU LEN", -1);
        if (cahaFocal > 0) return cahaFocal * 1000.0; // Converte metri -> mm

        foreach (var key in FocalKeys)
        {
            double val = metadata.GetDoubleValue(header, key, -1);
            if (val <= 0) continue;
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