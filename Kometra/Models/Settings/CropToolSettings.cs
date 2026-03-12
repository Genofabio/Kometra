using Kometra.Services.Processing.Coordinators;

namespace Kometra.Models.Settings;

public class CropToolSettings
{
    public CropMode Mode { get; set; } = CropMode.Static;
    public double Width { get; set; } = 500;
    public double Height { get; set; } = 500;
}