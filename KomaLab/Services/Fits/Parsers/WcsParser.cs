using System.Globalization;
using KomaLab.Models.Astrometry;
using nom.tam.fits;

namespace KomaLab.Services.Fits.Parsers;

// ---------------------------------------------------------------------------
// FILE: WcsParser.cs
// RUOLO: Parser
// DESCRIZIONE:
// Analizza l'header FITS per estrarre e costruire l'oggetto WcsData.
// Supporta proiezioni TAN/TPV, matrice CD e coefficienti di distorsione PV.
// Gestisce la sovrascrittura delle chiavi (comportamento standard FITS).
// ---------------------------------------------------------------------------

public static class WcsParser
{
    public static WcsData Parse(Header header)
    {
        var data = new WcsData();
        string ctype1 = "";

        var cursor = header.GetCursor();
        
        // Loop singolo su tutte le card
        while (cursor.MoveNext())
        {
            if (cursor.Current is not HeaderCard card) continue;

            string key = card.Key?.ToUpper().Trim() ?? "";
            string val = card.Value ?? "";

            // Switch su stringa è efficiente in C# moderno (hash jump)
            switch (key)
            {
                case "CTYPE1": 
                    // Rileva proiezione solo se valida
                    if (val.Contains("TAN") || val.Contains("TPV")) ctype1 = val; 
                    break;

                case "CRVAL1": data.RefRaDeg = ParseFitsDouble(val); break;
                case "CRVAL2": data.RefDecDeg = ParseFitsDouble(val); break;
                
                case "CRPIX1": data.RefPixelX = ParseFitsDouble(val); break;
                case "CRPIX2": data.RefPixelY = ParseFitsDouble(val); break;
                
                case "CD1_1": data.Cd1_1 = ParseFitsDouble(val); break;
                case "CD1_2": data.Cd1_2 = ParseFitsDouble(val); break;
                case "CD2_1": data.Cd2_1 = ParseFitsDouble(val); break;
                case "CD2_2": data.Cd2_2 = ParseFitsDouble(val); break;
            }

            // Gestione chiavi dinamiche PV (Distorsioni)
            if (key.StartsWith("PV") && TryParsePvKey(key, out int ax, out int k))
            {
                data.PvCoefficients[(ax, k)] = ParseFitsDouble(val);
            }
        }

        // Validazione finale
        if (string.IsNullOrEmpty(ctype1))
        {
            data.IsValid = false;
            // ProjectionType rimane Unknown di default
            return data;
        }

        data.ProjectionType = ctype1.Contains("TPV") ? WcsProjectionType.Tpv : WcsProjectionType.Tan;
        data.IsValid = true;
        return data;
    }

    // --- Helpers Interni ---

    /// <summary>
    /// Parsa un double FITS pulendo commenti inline e apici.
    /// </summary>
    private static double ParseFitsDouble(string val)
    {
        if (string.IsNullOrWhiteSpace(val)) return 0.0;
        
        int slashIndex = val.IndexOf('/');
        if (slashIndex > -1) val = val.Substring(0, slashIndex);
        
        val = val.Replace("'", "").Trim();
        
        return double.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out double result) ? result : 0.0;
    }

    /// <summary>
    /// Estrae indici da chiavi PVj_m (es. PV1_2).
    /// </summary>
    private static bool TryParsePvKey(string key, out int axis, out int k)
    {
        axis = 0; k = 0;
        if (key.Length < 4) return false; // Minimo "PV1_0"
        
        int underscore = key.IndexOf('_');
        if (underscore == -1) return false;

        // Sottostringa sicura
        return int.TryParse(key.Substring(2, underscore - 2), out axis) && 
               int.TryParse(key.Substring(underscore + 1), out k);
    }
}