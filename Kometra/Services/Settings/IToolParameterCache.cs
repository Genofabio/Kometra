using Kometra.Models.Settings;
using Kometra.ViewModels.ImageProcessing;

namespace Kometra.Services.Settings;

public interface IToolParametersCache
{
    CropToolSettings Crop { get; set; }
    AlignmentToolSettings Alignment { get; set; }
    EnhancementToolSettings Enhancement { get; set; }
    PosterizationToolSettings Posterization { get; set; }
    StarMaskingToolSettings StarMasking { get; set; }
    ExportToolSettings Export { get; set; }
    VideoExportToolSettings VideoExport { get; set; }
}