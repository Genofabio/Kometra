using Kometra.Models.Processing.Alignment;

namespace Kometra.Models.Settings;

public class AlignmentToolSettings
{
    public AlignmentTarget Target { get; set; } = AlignmentTarget.Comet;
    public AlignmentMode Mode { get; set; } = AlignmentMode.Automatic;
    public int SearchRadius { get; set; } = 100;
    public bool UseJplAstrometry { get; set; } = false;
    public bool CropToCommonArea { get; set; } = true;
    public string LastTargetName { get; set; } = string.Empty;
}