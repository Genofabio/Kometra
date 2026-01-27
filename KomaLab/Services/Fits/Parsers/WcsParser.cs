using System;
using System.Collections.Generic;
using KomaLab.Models.Astrometry;
using KomaLab.Models.Astrometry.Wcs;
using KomaLab.Models.Fits;
using KomaLab.Models.Fits.Structure;
using KomaLab.Services.Fits.Metadata;

namespace KomaLab.Services.Fits.Parsers;

public static class WcsParser
{
    public static WcsData Parse(FitsHeader header, IFitsMetadataService metadata)
    {
        var data = new WcsData();
        string ctype1 = metadata.GetStringValue(header, "CTYPE1");

        // Se manca CTYPE1, non c'è astrometria valida
        if (string.IsNullOrEmpty(ctype1)) return new WcsData { IsValid = false };

        // 1. Rilevamento Tipo Proiezione
        if (ctype1.Contains("-SIP")) 
            data.ProjectionType = WcsProjectionType.Sip;
        else if (ctype1.Contains("TPV") || ctype1.Contains("ZPN")) 
            data.ProjectionType = WcsProjectionType.Tpv;
        else 
            data.ProjectionType = WcsProjectionType.Tan;

        // 2. Coordinate di Riferimento (CRVAL / CRPIX) - Standard universale
        data.RefRaDeg = AstroParser.ParseDegrees(metadata.GetStringValue(header, "CRVAL1")) ?? 0;
        data.RefDecDeg = AstroParser.ParseDegrees(metadata.GetStringValue(header, "CRVAL2")) ?? 0;
        data.RefPixelX = metadata.GetDoubleValue(header, "CRPIX1");
        data.RefPixelY = metadata.GetDoubleValue(header, "CRPIX2");
        
        // 3. Matrice Lineare (CD o Fallback Legacy)
        ParseLinearMatrix(header, metadata, data);

        // 4. Coefficienti Distorsione TPV (PV Keywords)
        if (data.ProjectionType == WcsProjectionType.Tpv)
        {
            ParseTpvCoefficients(header, metadata, data);
        }

        // 5. Coefficienti Distorsione SIP (A/B Keywords) - [AGGIUNTA SCIENTIFICA]
        if (data.ProjectionType == WcsProjectionType.Sip)
        {
            ParseSipCoefficients(header, metadata, data);
        }

        data.IsValid = true;
        return data;
    }

    private static void ParseLinearMatrix(FitsHeader header, IFitsMetadataService metadata, WcsData data)
    {
        // Proviamo a leggere la matrice CD moderna
        double cd11 = metadata.GetDoubleValue(header, "CD1_1");
        double cd12 = metadata.GetDoubleValue(header, "CD1_2");
        double cd21 = metadata.GetDoubleValue(header, "CD2_1");
        double cd22 = metadata.GetDoubleValue(header, "CD2_2");

        bool hasCdMatrix = cd11 != 0 || cd12 != 0 || cd21 != 0 || cd22 != 0;

        if (hasCdMatrix)
        {
            data.Cd1_1 = cd11;
            data.Cd1_2 = cd12;
            data.Cd2_1 = cd21;
            data.Cd2_2 = cd22;
        }
        else
        {
            // Fallback: Calcoliamo la matrice CD dai vecchi parametri (CDELT + CROTA)
            ResolveLegacyRotation(header, metadata, data);
        }
    }

    private static void ResolveLegacyRotation(FitsHeader header, IFitsMetadataService metadata, WcsData data)
    {
        double cdelt1 = metadata.GetDoubleValue(header, "CDELT1", 1.0); 
        double cdelt2 = metadata.GetDoubleValue(header, "CDELT2", 1.0);
        double rotDeg = metadata.GetDoubleValue(header, "CROTA2", 0.0);
        
        double rad = rotDeg * (Math.PI / 180.0);
        double cos = Math.Cos(rad);
        double sin = Math.Sin(rad);

        data.Cd1_1 = cdelt1 * cos;
        data.Cd1_2 = Math.Abs(cdelt2) * Math.Sign(cdelt1) * sin;
        data.Cd2_1 = -Math.Abs(cdelt1) * Math.Sign(cdelt2) * sin;
        data.Cd2_2 = cdelt2 * cos;
    }

    private static void ParseTpvCoefficients(FitsHeader header, IFitsMetadataService metadata, WcsData data)
    {
        foreach (var card in header.Cards)
        {
            if (card.Key.StartsWith("PV") && TryParsePvKey(card.Key, out int ax, out int k))
            {
                data.PvCoefficients[(ax, k)] = metadata.GetDoubleValue(header, card.Key);
            }
        }
    }

    private static void ParseSipCoefficients(FitsHeader header, IFitsMetadataService metadata, WcsData data)
    {
        // 1. Ordini dei polinomi (A=Forward X, B=Forward Y)
        data.SipOrderA = metadata.GetIntValue(header, "A_ORDER", 0);
        data.SipOrderB = metadata.GetIntValue(header, "B_ORDER", 0);
        
        // 2. Ordini dei polinomi inversi (AP=Reverse X, BP=Reverse Y)
        data.SipOrderAp = metadata.GetIntValue(header, "AP_ORDER", 0);
        data.SipOrderBp = metadata.GetIntValue(header, "BP_ORDER", 0);

        // 3. Parsing matrici Forward
        if (data.SipOrderA > 0) ParsePolynomialMatrix(header, metadata, "A", data.SipOrderA, data.SipACoefficients);
        if (data.SipOrderB > 0) ParsePolynomialMatrix(header, metadata, "B", data.SipOrderB, data.SipBCoefficients);

        // 4. Parsing matrici Reverse (Opzionali ma utili per World->Pixel)
        if (data.SipOrderAp > 0) ParsePolynomialMatrix(header, metadata, "AP", data.SipOrderAp, data.SipApCoefficients);
        if (data.SipOrderBp > 0) ParsePolynomialMatrix(header, metadata, "BP", data.SipOrderBp, data.SipBpCoefficients);
    }

    private static void ParsePolynomialMatrix(
        FitsHeader header, 
        IFitsMetadataService metadata, 
        string prefix, 
        int order, 
        Dictionary<(int, int), double> targetDict)
    {
        // Lo standard SIP definisce le chiavi come A_p_q (es. A_2_0)
        // Poiché l'ordine è basso (tipicamente <= 4), iteriamo gli indici invece di scansionare l'header
        for (int p = 0; p <= order; p++)
        {
            for (int q = 0; q <= order; q++)
            {
                // Ottimizzazione: SIP standard richiede p+q <= order, ma alcune implementazioni (es. SCAMP)
                // possono includere termini incrociati extra. Noi proviamo a leggerli tutti nel quadrato.
                if (p + q > order && p != 0 && q != 0) continue; 

                string key = $"{prefix}_{p}_{q}";
                double val = metadata.GetDoubleValue(header, key, 0.0);

                // Memorizziamo solo i coefficienti non nulli per risparmiare memoria
                if (Math.Abs(val) > 1e-15)
                {
                    targetDict[(p, q)] = val;
                }
            }
        }
    }

    private static bool TryParsePvKey(string key, out int axis, out int k)
    {
        axis = 0; k = 0;
        int underscore = key.IndexOf('_');
        if (underscore == -1) return false;

        return int.TryParse(key.Substring(2, underscore - 2), out axis) && 
               int.TryParse(key.Substring(underscore + 1), out k);
    }
}