using Kometra.Models.Primitives;

namespace Kometra.Models.Processing.Alignment;

public class AlignmentProgressReport
{
    public int CurrentIndex { get; set; }
    public int TotalCount { get; set; }
    public string? FileName { get; set; }
    public Point2D? FoundCenter { get; set; }
    public string? Message { get; set; }
}