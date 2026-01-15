using System;
using System.Globalization;
using KomaLab.Models.Fits;

namespace KomaLab.Services.Fits.Parsers;

public static class OpticalParser
{
    private static readonly string[] FocalKeys = { "FOCALLEN", "FOCAL", "HIERARCH CAHA TEL FOCU LEN" };
    private static readonly string[] PixelKeys = { "XPIXSZ", "PIXSIZE", "CCDPSIZ", "INSTRPIX", "HIERARCH CAHA DET CCD1 PSZX" };

    public static double? ParseFocalLength(FitsHeader header)
    {
        foreach (var key in FocalKeys)
        {
            var val = header.GetValue<double>(key);
            if (!val.HasValue || val <= 0) continue;

            // Logica specifica CAHA: se la chiave contiene 'CAHA' e 'LEN', il valore è in metri
            if (key.Contains("CAHA") && key.Contains("LEN"))
            {
                return val.Value * 1000.0; // Conversione m -> mm
            }

            return val.Value;
        }
        return null;
    }

    public static double? ParsePixelSize(FitsHeader header)
    {
        foreach (var key in PixelKeys)
        {
            var val = header.GetValue<double>(key);
            if (val.HasValue && val > 0) return val.Value;
        }
        return null;
    }
}