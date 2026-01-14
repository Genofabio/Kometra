using System.Globalization;
using KomaLab.Models.Astrometry;
using KomaLab.Models.Fits;

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
    public static WcsData Parse(FitsHeader header)
    {
        var data = new WcsData();
        string ctype1 = "";

        foreach (var card in header.Cards)
        {
            // Key è già Upper e Trimmed grazie al FitsReader
            string key = card.Key;
            
            // Ottimizzazione: se la chiave è vuota o il valore nullo, salta
            if (string.IsNullOrEmpty(key) || string.IsNullOrWhiteSpace(card.Value)) continue;

            // Switch su stringa è compilato in una Hash Table (molto veloce)
            switch (key)
            {
                case "CTYPE1": 
                    // CTYPE è una stringa, quindi avrà gli apici (es 'RA---TAN'). Li rimuoviamo.
                    string valStr = card.Value.Replace("'", "").Trim();
                    if (valStr.Contains("TAN") || valStr.Contains("TPV")) ctype1 = valStr; 
                    break;

                // Per i numeri usiamo un helper locale inline o statico
                case "CRVAL1": data.RefRaDeg = ParseDouble(card.Value); break;
                case "CRVAL2": data.RefDecDeg = ParseDouble(card.Value); break;
                case "CRPIX1": data.RefPixelX = ParseDouble(card.Value); break;
                case "CRPIX2": data.RefPixelY = ParseDouble(card.Value); break;
                
                case "CD1_1": data.Cd1_1 = ParseDouble(card.Value); break;
                case "CD1_2": data.Cd1_2 = ParseDouble(card.Value); break;
                case "CD2_1": data.Cd2_1 = ParseDouble(card.Value); break;
                case "CD2_2": data.Cd2_2 = ParseDouble(card.Value); break;
            }

            // Gestione Distorsioni PV (es. PV1_1, PV2_3)
            // Controllo rapido: deve iniziare con PV e avere lunghezza minima
            if (key.Length >= 5 && key.StartsWith("PV") && TryParsePvKey(key, out int ax, out int k))
            {
                data.PvCoefficients[(ax, k)] = ParseDouble(card.Value);
            }
        }

        if (string.IsNullOrEmpty(ctype1))
        {
            data.IsValid = false;
            return data;
        }

        data.ProjectionType = ctype1.Contains("TPV") ? WcsProjectionType.Tpv : WcsProjectionType.Tan;
        data.IsValid = true;
        return data;
    }

    // Helper snellito: FitsReader ha già tolto i commenti, dobbiamo solo togliere gli apici
    private static double ParseDouble(string val)
    {
        // Rimuove apici se presenti (alcuni software scrivono numeri come stringhe '123.45')
        // Usiamo Span/Memory se volessimo ultra-performance, ma Replace qui va bene.
        val = val.Replace("'", "").Trim();
        return double.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out double res) ? res : 0.0;
    }

    private static bool TryParsePvKey(string key, out int axis, out int k)
    {
        // Formato atteso: PVj_m (es. PV1_2)
        axis = 0; k = 0;
        int underscore = key.IndexOf('_');
        if (underscore == -1) return false;

        // Parsing sicuro delle sottostringhe numeriche
        return int.TryParse(key.Substring(2, underscore - 2), out axis) && 
               int.TryParse(key.Substring(underscore + 1), out k);
    }
}