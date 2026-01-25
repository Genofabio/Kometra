using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KomaLab.Models.Fits;

namespace KomaLab.Models.Processing.Enhancement;

/// <summary>
/// Contiene tutti i parametri necessari per gli algoritmi di estrazione strutture.
/// </summary>
public class StructureExtractionParameters
{
    // --- Larson-Sekanina ---
    public double RotationAngle { get; set; } = 2.0;
    public double ShiftX { get; set; } = 0.0;
    public double ShiftY { get; set; } = 0.0;
    
    // --- Median / Unsharp ---
    public int KernelSize { get; set; } = 15;

    // --- RVSF (Parametri Singoli o Minimi per il Mosaico) ---
    public double ParamA_1 { get; set; } = 1.0;
    public double ParamB_1 { get; set; } = 1.0;
    public double ParamN_1 { get; set; } = 1.0;

    // --- RVSF (Parametri Massimi per il Mosaico) ---
    public double ParamA_2 { get; set; } = 5.0;
    public double ParamB_2 { get; set; } = 0.1;
    public double ParamN_2 { get; set; } = 0.5;

    public bool UseLog { get; set; } = true;
}

