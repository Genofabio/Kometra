using Kometra.Models.Export;

namespace Kometra.Models.Settings;

public class ExportToolSettings
{
    public ExportFormat SelectedFormat { get; set; } = ExportFormat.FITS;
    public bool MergeIntoSingleFile { get; set; } = false;
    public FitsCompressionMode SelectedCompression { get; set; } = FitsCompressionMode.None;
    public int JpegQuality { get; set; } = 95;
    public string? StretchMode { get; set; }
    public string? OutputDirectory { get; set; }
}