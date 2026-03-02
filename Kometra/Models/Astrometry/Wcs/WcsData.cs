using System;
using System.Collections.Generic;

namespace Kometra.Models.Astrometry.Wcs;

// ---------------------------------------------------------------------------
// FILE: WcsData.cs
// DESCRIZIONE:
// Contiene i dati del World Coordinate System (WCS) estratti dall'header FITS.
// Supporta:
// 1. Matrice Lineare (CD o PC+CDELT)
// 2. Distorsioni TPV (Polynomial Variant)
// 3. Distorsioni SIP (Simple Imaging Polynomial)
// ---------------------------------------------------------------------------
public class WcsData
{
    /// <summary>
    /// Indica se i dati WCS sono stati letti correttamente e sono utilizzabili.
    /// </summary>
    public bool IsValid { get; set; } = false;

    /// <summary>
    /// Il tipo di proiezione geometrica (es. TAN, TPV, SIP).
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

    // --- DISTORSIONI TPV (PV Keywords) ---

    /// <summary>
    /// Coefficienti di distorsione TPV (se presenti).
    /// La chiave è una tupla (Asse, Termine K). Es: PV1_1 -> (Axis: 1, K: 1).
    /// </summary>
    public Dictionary<(int Axis, int K), double> PvCoefficients { get; } = new();

    // --- DISTORSIONI SIP (A/B Keywords - Forward) ---
    // Usati per trasformare Pixel -> Sky.

    /// <summary>
    /// Ordine del polinomio A (Keyword A_ORDER).
    /// </summary>
    public int SipOrderA { get; set; }

    /// <summary>
    /// Ordine del polinomio B (Keyword B_ORDER).
    /// </summary>
    public int SipOrderB { get; set; }

    /// <summary>
    /// Coefficienti SIP A. Chiave (p, q) corrispondente a u^p * v^q.
    /// Keyword es: A_2_0 -> (2, 0)
    /// </summary>
    public Dictionary<(int p, int q), double> SipACoefficients { get; } = new();

    /// <summary>
    /// Coefficienti SIP B. Chiave (p, q) corrispondente a u^p * v^q.
    /// Keyword es: B_0_2 -> (0, 2)
    /// </summary>
    public Dictionary<(int p, int q), double> SipBCoefficients { get; } = new();

    // --- DISTORSIONI SIP (AP/BP Keywords - Reverse) ---
    // Usati per trasformare Sky -> Pixel (opzionali ma veloci).

    /// <summary>
    /// Ordine del polinomio AP (Keyword AP_ORDER).
    /// </summary>
    public int SipOrderAp { get; set; }

    /// <summary>
    /// Ordine del polinomio BP (Keyword BP_ORDER).
    /// </summary>
    public int SipOrderBp { get; set; }

    /// <summary>
    /// Coefficienti SIP AP (Inverse A).
    /// </summary>
    public Dictionary<(int p, int q), double> SipApCoefficients { get; } = new();

    /// <summary>
    /// Coefficienti SIP BP (Inverse B).
    /// </summary>
    public Dictionary<(int p, int q), double> SipBpCoefficients { get; } = new();


    // --- HELPER ESPLICITI ---

    /// <summary>
    /// Determina esplicitamente se questo WCS include termini di distorsione
    /// che richiedono calcoli non lineari (TPV o SIP).
    /// </summary>
    public bool HasDistortion
    {
        get
        {
            if (ProjectionType == WcsProjectionType.Tpv && PvCoefficients.Count > 0) return true;
            
            if (ProjectionType == WcsProjectionType.Sip)
            {
                // Se è SIP, controlliamo se ci sono coefficienti A o B
                return SipACoefficients.Count > 0 || SipBCoefficients.Count > 0;
            }

            return false;
        }
    }

    /// <summary>
    /// Calcola la scala approssimativa del pixel in arcosecondi/pixel 
    /// basandosi sul determinante della matrice CD.
    /// </summary>
    public double PixelScaleArcsec
    {
        get
        {
            // Determinante = (cd1_1 * cd2_2) - (cd1_2 * cd2_1)
            // Poiché i CD sono in gradi/pixel, moltiplichiamo per 3600 per avere arcsec.
            double det = Math.Abs((Cd1_1 * Cd2_2) - (Cd1_2 * Cd2_1));
            return Math.Sqrt(det) * 3600.0;
        }
    }
}