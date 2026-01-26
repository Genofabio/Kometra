using System;

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
    public int KernelSize { get; set; } = 25;

    // --- RVSF Common ---
    public bool UseLog { get; set; } = true;
    public double ParamA_1 { get; set; } = 1.0;
    public double ParamB_1 { get; set; } = 0.2;
    public double ParamN_1 { get; set; } = 1.0;
    public double ParamA_2 { get; set; } = 5.0;
    public double ParamB_2 { get; set; } = 0.5;
    public double ParamN_2 { get; set; } = 0.5;

    // --- Frangi Vesselness (Getti Curvi) ---
    /// <summary> Spessore previsto della struttura da rilevare (in pixel). </summary>
    public double FrangiSigma { get; set; } = 1.5;
    /// <summary> Selettività verso le linee (0.1 - 1.0). Più è basso, più ignora i blob. </summary>
    public double FrangiBeta { get; set; } = 0.5;
    /// <summary> Sensibilità al rumore. Valore alto = filtra più rumore. </summary>
    public double FrangiC { get; set; } = 0.001;

    // --- White Top-Hat (Estrazione Getti) ---
    /// <summary> Dimensione elemento strutturante (deve essere > larghezza getto). </summary>
    public int TopHatKernelSize { get; set; } = 21;

    // --- CLAHE (Contrasto Locale 16-bit) ---
    public double ClaheClipLimit { get; set; } = 3.0;
    public int ClaheTileSize { get; set; } = 8;
    
    // --- Structure Tensor Enhancement (Esaltazione Coerenza) ---
    /// <summary> Scala del gradiente locale (dettaglio fine). </summary>
    public int TensorSigma { get; set; } = 1;
    /// <summary> Scala di integrazione (lunghezza della struttura coerente). </summary>
    public int TensorRho { get; set; } = 3;

    // --- Local Normalization (LSN - 100% Float) ---
    /// <summary> Finestra di analisi statistica (es. 41 = 41x41 pixel). </summary>
    public int LocalNormWindowSize { get; set; } = 41;
    /// <summary> Guadagno del contrasto applicato alla deviazione standard locale. </summary>
    public double LocalNormIntensity { get; set; } = 1.0;
}