using nom.tam.fits;
using System;
using System.Diagnostics;
using System.Globalization;
using KomaLab.Models; 

namespace KomaLab.Services.Astrometry;

public static class WcsHeaderParser
{
    public static WcsData Parse(Header header)
    {
        var data = new WcsData();
        
        // Variabili temporanee per memorizzare l'ultima occorrenza trovata
        string foundCtype1 = "";
        double? foundCrval1 = null;
        double? foundCrval2 = null;
        double? foundCrpix1 = null;
        double? foundCrpix2 = null;
        
        // Scansioniamo l'intero header manualmente per prendere L'ULTIMA occorrenza valida
        // (Astrometry.net scrive i suoi dati alla fine del file)
        var cursor = header.GetCursor();
        while (cursor.MoveNext())
        {
            HeaderCard? card = null;
            if (cursor.Current is HeaderCard hc) card = hc;
            else if (cursor.Current is System.Collections.DictionaryEntry de && de.Value is HeaderCard hcd) card = hcd;

            if (card == null) continue;

            string key = card.Key.ToUpper().Trim();
            string value = card.Value;

            // 1. Cerca CTYPE1 (Proiezione)
            if (key == "CTYPE1")
            {
                // Ignoriamo 'PIXEL' che è roba vecchia. Vogliamo TAN o TPV.
                if (value.Contains("TAN") || value.Contains("TPV"))
                {
                    foundCtype1 = value;
                }
            }

            // 2. Cerca CRVAL (Coordinate)
            if (key == "CRVAL1") foundCrval1 = ParseDouble(value);
            if (key == "CRVAL2") foundCrval2 = ParseDouble(value);

            // 3. Cerca CRPIX (Pixel Riferimento)
            if (key == "CRPIX1") foundCrpix1 = ParseDouble(value);
            if (key == "CRPIX2") foundCrpix2 = ParseDouble(value);

            // 4. Matrice CD o CDELT
            if (key == "CD1_1") data.Cd1_1 = ParseDouble(value);
            if (key == "CD1_2") data.Cd1_2 = ParseDouble(value);
            if (key == "CD2_1") data.Cd2_1 = ParseDouble(value);
            if (key == "CD2_2") data.Cd2_2 = ParseDouble(value);

            // 5. Coefficienti PV (Distorsione)
            if (key.StartsWith("PV"))
            {
                if (ParsePvKey(key, out int axis, out int k))
                {
                    data.PvCoefficients[(axis, k)] = ParseDouble(value);
                }
            }
        }

        // --- VALIDAZIONE ---

        // Se non abbiamo trovato un CTYPE valido (TAN/TPV), falliamo
        if (string.IsNullOrEmpty(foundCtype1))
        {
            Debug.WriteLine("[WcsParser] FALLITO: Nessun CTYPE1 valido trovato (o era solo PIXEL).");
            data.IsValid = false;
            return data;
        }

        // Impostiamo il tipo
        if (foundCtype1.Contains("TPV")) data.ProjectionType = "TPV";
        else data.ProjectionType = "TAN";

        // Assegniamo i valori trovati (o 0.0 se mancanti, ma dovrebbero esserci)
        data.RefRaDeg = foundCrval1 ?? 0.0;
        data.RefDecDeg = foundCrval2 ?? 0.0;
        data.RefPixelX = foundCrpix1 ?? 0.0;
        data.RefPixelY = foundCrpix2 ?? 0.0;

        // Fallback per CD Matrix se non trovata (usa CDELT se presenti nel FITS, ma il tuo ha CD)
        // Nel tuo caso specifico hai CD1_1 ecc, quindi dovrebbero essere già popolati dal loop.
        // Se fossero tutti 0, potremmo controllare i vecchi CDELT, ma il tuo header è moderno.
        
        data.IsValid = true;
        Debug.WriteLine($"[WcsParser] SUCCESSO: Tipo={data.ProjectionType}, RA={data.RefRaDeg}, CRPIX={data.RefPixelX}");
        return data;
    }

    // Helper robusto per il parsing numerico (indipendente dalla cultura locale)
    private static double ParseDouble(string val)
    {
        if (string.IsNullOrEmpty(val)) return 0.0;
        // Rimuovi commenti inline se presenti (es "1.0 / commento")
        int slashIndex = val.IndexOf('/');
        if (slashIndex > -1) val = val.Substring(0, slashIndex);
        
        // Rimuovi apici se presenti
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
        // Key attesa: PV1_1, PV2_0, ecc.
        if (key.Length < 4 || !key.StartsWith("PV")) return false;
        
        // Il formato nel tuo header è PV1_1 (senza underscore prima dell'asse) o PV1_1?
        // Nel dump vedo: "PV1_0", "PV1_1". C'è l'underscore tra asse e k.
        
        int underscore = key.IndexOf('_');
        if (underscore == -1) return false;

        // Asse è tra 'PV' e '_' (es. PV1_ -> '1')
        string axisPart = key.Substring(2, underscore - 2);
        // K è dopo '_'
        string kPart = key.Substring(underscore + 1);

        if (int.TryParse(axisPart, out axis) && int.TryParse(kPart, out k))
            return true;
            
        return false;
    }
}