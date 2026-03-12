using Kometra.Models.Processing.Enhancement;

namespace Kometra.Models.Settings;

public class EnhancementToolSettings
{
    public ImageEnhancementMode? LastMode { get; set; }
    public double RotationAngle { get; set; } = 5.0;
    public double ShiftX { get; set; } = 0.0;
    public double ShiftY { get; set; } = 0.0;
    public bool UseLogScale { get; set; } = true;
    
    public double RvsfA_1 { get; set; } = 1.0;
    public double RvsfA_2 { get; set; } = 5.0;
    public double RvsfB_1 { get; set; } = 0.2;
    public double RvsfB_2 { get; set; } = 0.5;
    public double RvsfN_1 { get; set; } = 1.0;
    public double RvsfN_2 { get; set; } = 0.5;
    
    public int RadialSubsampling { get; set; } = 5;
    public double RadialMaxRadius { get; set; } = 100.0;
    
    public double AzimuthalRejSigma { get; set; } = 3.0;
    public double AzimuthalNormSigma { get; set; } = 20.0;
    
    public double FrangiSigma { get; set; } = 1.5;
    public double FrangiBeta { get; set; } = 0.5;
    public double FrangiC { get; set; } = 0.001;
    public int TensorSigma { get; set; } = 1;
    public int TensorRho { get; set; } = 3;
    public int TopHatKernelSize { get; set; } = 21;
    
    public int KernelSize { get; set; } = 25; 
    public double ClaheClipLimit { get; set; } = 3.0;
    public int ClaheTileSize { get; set; } = 8;
    public int LocalNormWindowSize { get; set; } = 41;
    public double LocalNormIntensity { get; set; } = 1.0;
}