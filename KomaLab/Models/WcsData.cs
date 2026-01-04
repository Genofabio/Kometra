using System.Collections.Generic;

namespace KomaLab.Models; // O Services.Astrometry se l'hai spostato

public class WcsData
{
    public bool IsValid { get; set; } = false;
    public string ProjectionType { get; set; } = "TAN";

    // Riferimenti
    public double RefPixelX { get; set; }
    public double RefPixelY { get; set; }
    public double RefRaDeg { get; set; }
    public double RefDecDeg { get; set; }

    // Matrice CD
    public double Cd1_1 { get; set; }
    public double Cd1_2 { get; set; }
    public double Cd2_1 { get; set; }
    public double Cd2_2 { get; set; }

    // --- NUOVO: Coefficienti di Distorsione TPV ---
    // La chiave è (asse, termine k). Es: PV1_1 -> (1, 1)
    public Dictionary<(int Axis, int K), double> PvCoefficients { get; } = new();
}