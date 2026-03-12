namespace Kometra.Models.Settings;

public class StarMaskingToolSettings
{
    public double CometThresholdSigma { get; set; } = 3.0;
    public int CometDilation { get; set; } = 0;
    public double StarThresholdSigma { get; set; } = 3.0;
    public int StarDilation { get; set; } = 0;
    public int MinStarDiameter { get; set; } = 2;
}