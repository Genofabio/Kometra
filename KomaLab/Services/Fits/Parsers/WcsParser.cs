using System;
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

        data.ProjectionType = ctype1.Contains("TPV") ? WcsProjectionType.Tpv : WcsProjectionType.Tan;

        // Coordinate di Riferimento (CRVAL / CRPIX) - Standard universale
        data.RefRaDeg = AstroParser.ParseDegrees(metadata.GetStringValue(header, "CRVAL1")) ?? 0;
        data.RefDecDeg = AstroParser.ParseDegrees(metadata.GetStringValue(header, "CRVAL2")) ?? 0;
        data.RefPixelX = metadata.GetDoubleValue(header, "CRPIX1");
        data.RefPixelY = metadata.GetDoubleValue(header, "CRPIX2");
        
        // --- LOGICA DI RISOLUZIONE MATRICE DI TRASFORMAZIONE ---
        
        // 1. Proviamo a leggere la matrice CD moderna
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
            // 2. Fallback: Calcoliamo la matrice CD dai vecchi parametri (CDELT + CROTA)
            //    Questo supporta file vecchi o generati da software semplici.
            ResolveLegacyRotation(header, metadata, data);
        }

        // --- COEFFICIENTI DI DISTORSIONE (PV) ---
        foreach (var card in header.Cards)
        {
            if (card.Key.StartsWith("PV") && TryParsePvKey(card.Key, out int ax, out int k))
            {
                data.PvCoefficients[(ax, k)] = metadata.GetDoubleValue(header, card.Key);
            }
        }

        data.IsValid = true;
        return data;
    }

    /// <summary>
    /// Converte CDELT/CROTA (Legacy) in matrice CD (Standard Moderno).
    /// </summary>
    private static void ResolveLegacyRotation(FitsHeader header, IFitsMetadataService metadata, WcsData data)
    {
        // Lettura Scala (gradi per pixel)
        // Default a 1.0 se manca, per evitare divisioni per zero, ma segnando l'anomalia
        double cdelt1 = metadata.GetDoubleValue(header, "CDELT1", 1.0); 
        double cdelt2 = metadata.GetDoubleValue(header, "CDELT2", 1.0);

        // Lettura Rotazione (in gradi)
        // Nota: CROTA2 è lo standard de-facto, CROTA1 è rarissimo
        double rotDeg = metadata.GetDoubleValue(header, "CROTA2", 0.0);
        
        // Conversione in radianti
        // Nota: La rotazione WCS è definita positiva verso Nord -> Est (senso antiorario)
        // Ma spesso nei CCD l'asse Y è invertito, quindi va gestito con attenzione.
        // La formula standard IAU per convertire CDELT/CROTA in CD è:
        
        double rad = rotDeg * (Math.PI / 180.0);
        double cos = Math.Cos(rad);
        double sin = Math.Sin(rad);

        // A volte esiste anche una matrice PC parziale (PC1_1...)
        // Per semplicità qui assumiamo il caso classico puro (solo rotazione semplice)
        
        // Formule standard:
        // CD1_1 = CDELT1 * cos(rot)
        // CD1_2 = |CDELT2| * sin(rot)  <-- Nota: CDELT2 spesso include il segno negativo per l'asse Dec
        // CD2_1 = |CDELT1| * -sin(rot)
        // CD2_2 = CDELT2 * cos(rot)

        // Implementazione robusta semplificata (assume assi ortogonali standard)
        data.Cd1_1 = cdelt1 * cos;
        data.Cd1_2 = Math.Abs(cdelt2) * Math.Sign(cdelt1) * sin; // Gestione segni incrociati
        data.Cd2_1 = -Math.Abs(cdelt1) * Math.Sign(cdelt2) * sin;
        data.Cd2_2 = cdelt2 * cos;
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