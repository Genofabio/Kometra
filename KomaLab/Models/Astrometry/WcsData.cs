using System;
using System.Collections.Generic;

namespace KomaLab.Models.Astrometry;

// ---------------------------------------------------------------------------
// FILE: WcsData.cs
// DESCRIZIONE:
// Contiene i dati del World Coordinate System (WCS) estratti dall'header FITS.
// Supporta la matrice di trasformazione lineare (CD) e le distorsioni (PV).
// ---------------------------------------------------------------------------
public class WcsData
{
    /// <summary>
    /// Indica se i dati WCS sono stati letti correttamente e sono utilizzabili.
    /// </summary>
    public bool IsValid { get; set; } = false;

    /// <summary>
    /// Il tipo di proiezione geometrica (es. TAN, TPV).
    /// </summary>
    public WcsProjectionType ProjectionType { get; set; } = WcsProjectionType.Tan;

    // --- RIFERIMENTI (CRPIX, CRVAL) ---

    /// <summary>
    /// Coordinata X del pixel di riferimento (CRPIX1).
    /// </summary>
    public double RefPixelX { get; set; }

    /// <summary>
    /// Coordinata Y del pixel di riferimento (CRPIX2).
    /// </summary>
    public double RefPixelY { get; set; }

    /// <summary>
    /// Ascensione Retta del punto di riferimento in gradi (CRVAL1).
    /// </summary>
    public double RefRaDeg { get; set; }

    /// <summary>
    /// Declinazione del punto di riferimento in gradi (CRVAL2).
    /// </summary>
    public double RefDecDeg { get; set; }

    // --- MATRICE DI TRASFORMAZIONE LINEARE (CD Matrix) ---

    public double Cd1_1 { get; set; }
    public double Cd1_2 { get; set; }
    public double Cd2_1 { get; set; }
    public double Cd2_2 { get; set; }

    // --- DISTORSIONI ---

    /// <summary>
    /// Coefficienti di distorsione TPV (se presenti).
    /// La chiave è una tupla (Asse, Termine K). Es: PV1_1 -> (Axis: 1, K: 1).
    /// </summary>
    public Dictionary<(int Axis, int K), double> PvCoefficients { get; } = new();

    // --- HELPER ESPLICITI ---

    /// <summary>
    /// Determina esplicitamente se questo WCS include termini di distorsione
    /// che richiedono calcoli non lineari.
    /// </summary>
    public bool HasDistortion => 
        ProjectionType == WcsProjectionType.Tpv && PvCoefficients.Count > 0;

    /// <summary>
    /// Calcola la scala approssimativa del pixel in arcosecondi/pixel 
    /// basandosi sul determinante della matrice CD.
    /// </summary>
    public double PixelScaleArcsec
    {
        get
        {
            // Calcolo approssimativo: radice quadrata del determinante per ottenere la scala media
            // Determinante = (cd1_1 * cd2_2) - (cd1_2 * cd2_1)
            // Poiché i CD sono in gradi/pixel, moltiplichiamo per 3600 per avere arcsec.
            double det = Math.Abs((Cd1_1 * Cd2_2) - (Cd1_2 * Cd2_1));
            return Math.Sqrt(det) * 3600.0;
        }
    }
}