using Kometra.Models.Export;

namespace Kometra.Models.Settings;

public class VideoExportToolSettings
{
    public VideoContainer SelectedContainer { get; set; } = VideoContainer.MP4;
    public VideoCodec SelectedCodec { get; set; } = VideoCodec.H264;
    public double Fps { get; set; } = 24.0;
    public double ScaleFactor { get; set; } = 1.0;
    public string? OutputFolder { get; set; }
}