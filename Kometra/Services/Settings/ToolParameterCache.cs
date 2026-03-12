using Kometra.Models.Settings;

namespace Kometra.Services.Settings;

public class ToolParametersCache : IToolParametersCache
{
    // Inizializziamo subito le classi con i loro default
    public CropToolSettings Crop { get; set; } = new();
    public AlignmentToolSettings Alignment { get; set; } = new();
    public EnhancementToolSettings Enhancement { get; set; } = new();
    public PosterizationToolSettings Posterization { get; set; } = new();
    public StarMaskingToolSettings StarMasking { get; set; } = new();
    public ExportToolSettings Export { get; set; } = new();
    public VideoExportToolSettings VideoExport { get; set; } = new();
}