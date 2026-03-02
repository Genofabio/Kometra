namespace Kometra.Models.Processing.Enhancement;

public class ImageEnhancementParameters
{
    // --- Radial & Rotational ---
    public double RotationAngle { get; set; } = 2.0;
    public double ShiftX { get; set; } = 0.0;
    public double ShiftY { get; set; } = 0.0;
    public bool UseLog { get; set; } = true;
    
    // RVSF Parameters
    public double ParamA_1 { get; set; } = 1.0;
    public double ParamB_1 { get; set; } = 0.2;
    public double ParamN_1 { get; set; } = 1.0;
    public double ParamA_2 { get; set; } = 5.0;
    public double ParamB_2 { get; set; } = 0.5;
    public double ParamN_2 { get; set; } = 0.5;

    /// <summary>
    /// Ora utilizzato SOLO per Median Coma Model come 'Angular Quality'.
    /// </summary>
    public int RadialSubsampling { get; set; } = 5;

    /// <summary>
    /// Raggio massimo per il blending in R.W.M. e M.C.M.
    /// Changed to Double per precisione sub-pixel nelle normalizzazioni.
    /// </summary>
    public double RadialMaxRadius { get; set; } = 100.0;

    // Azimuthal Parameters
    public double AzimuthalRejSigma { get; set; } = 3.0;
    public double AzimuthalNormSigma { get; set; } = 20.0;

    // --- Feature Extraction ---
    public double FrangiSigma { get; set; } = 1.5;
    public double FrangiBeta { get; set; } = 0.5;
    public double FrangiC { get; set; } = 0.001;
    public int TensorSigma { get; set; } = 1;
    public int TensorRho { get; set; } = 3;
    public int TopHatKernelSize { get; set; } = 21;

    // --- Local Contrast ---
    public int KernelSize { get; set; } = 25;
    public double ClaheClipLimit { get; set; } = 3.0;
    public int ClaheTileSize { get; set; } = 8;
    public int LocalNormWindowSize { get; set; } = 41;
    public double LocalNormIntensity { get; set; } = 1.0;
}