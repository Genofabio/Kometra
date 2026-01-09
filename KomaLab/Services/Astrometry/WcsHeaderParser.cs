using nom.tam.fits;
using System;
using System.Diagnostics;
using System.Globalization;
using KomaLab.Models;
using KomaLab.Models.Astrometry;

namespace KomaLab.Services.Astrometry;

public static class WcsHeaderParser
{
    public static WcsData Parse(Header header)
    {
        var data = new WcsData();
        
        string foundCtype1 = "";
        double? foundCrval1 = null;
        double? foundCrval2 = null;
        double? foundCrpix1 = null;
        double? foundCrpix2 = null;
        
        // Scansione manuale per trovare l'ultima occorrenza (Astrometry.net style)
        var cursor = header.GetCursor();
        while (cursor.MoveNext())
        {
            HeaderCard? card = null;
            if (cursor.Current is HeaderCard hc) card = hc;
            else if (cursor.Current is System.Collections.DictionaryEntry de && de.Value is HeaderCard hcd) card = hcd;

            if (card == null) continue;

            string key = card.Key.ToUpper().Trim();
            string value = card.Value;

            if (key == "CTYPE1")
            {
                if (value.Contains("TAN") || value.Contains("TPV"))
                {
                    foundCtype1 = value;
                }
            }

            if (key == "CRVAL1") foundCrval1 = ParseDouble(value);
            if (key == "CRVAL2") foundCrval2 = ParseDouble(value);
            if (key == "CRPIX1") foundCrpix1 = ParseDouble(value);
            if (key == "CRPIX2") foundCrpix2 = ParseDouble(value);

            if (key == "CD1_1") data.Cd1_1 = ParseDouble(value);
            if (key == "CD1_2") data.Cd1_2 = ParseDouble(value);
            if (key == "CD2_1") data.Cd2_1 = ParseDouble(value);
            if (key == "CD2_2") data.Cd2_2 = ParseDouble(value);

            if (key.StartsWith("PV"))
            {
                if (ParsePvKey(key, out int axis, out int k))
                {
                    data.PvCoefficients[(axis, k)] = ParseDouble(value);
                }
            }
        }

        // --- VALIDAZIONE & CONVERSIONE ESPLICITA ---

        if (string.IsNullOrEmpty(foundCtype1))
        {
            Debug.WriteLine("[WcsParser] FALLITO: Nessun CTYPE1 valido trovato.");
            data.IsValid = false;
            // Impostiamo Unknown esplicitamente
            data.ProjectionType = WcsProjectionType.Unknown;
            return data;
        }

        // CORREZIONE: Mappatura Stringa FITS -> Enum WcsProjectionType
        if (foundCtype1.Contains("TPV")) 
        {
            data.ProjectionType = WcsProjectionType.Tpv;
        }
        else if (foundCtype1.Contains("TAN")) 
        {
            data.ProjectionType = WcsProjectionType.Tan;
        }
        else 
        {
            // Caso fallback (es. SIP non ancora implementato)
            data.ProjectionType = WcsProjectionType.Unknown;
        }

        data.RefRaDeg = foundCrval1 ?? 0.0;
        data.RefDecDeg = foundCrval2 ?? 0.0;
        data.RefPixelX = foundCrpix1 ?? 0.0;
        data.RefPixelY = foundCrpix2 ?? 0.0;

        data.IsValid = (data.ProjectionType != WcsProjectionType.Unknown);
        
        if (data.IsValid)
            Debug.WriteLine($"[WcsParser] SUCCESSO: Tipo={data.ProjectionType}, RA={data.RefRaDeg}, CRPIX={data.RefPixelX}");
        
        return data;
    }

    private static double ParseDouble(string val)
    {
        if (string.IsNullOrEmpty(val)) return 0.0;
        int slashIndex = val.IndexOf('/');
        if (slashIndex > -1) val = val.Substring(0, slashIndex);
        
        val = val.Replace("'", "").Trim();

        if (double.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out double result))
        {
            return result;
        }
        return 0.0;
    }

    private static bool ParsePvKey(string key, out int axis, out int k)
    {
        axis = 0; k = 0;
        if (key.Length < 4 || !key.StartsWith("PV")) return false;
        
        int underscore = key.IndexOf('_');
        if (underscore == -1) return false;

        string axisPart = key.Substring(2, underscore - 2);
        string kPart = key.Substring(underscore + 1);

        if (int.TryParse(axisPart, out axis) && int.TryParse(kPart, out k))
            return true;
            
        return false;
    }
}