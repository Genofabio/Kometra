using KomaLab.Models.Primitives;

namespace KomaLab.Models.Processing;

public class AlignmentProgressReport
{
    public int CurrentIndex { get; set; }
    public int TotalCount { get; set; }
    public string? FileName { get; set; }
    public Point2D? FoundCenter { get; set; }
    public string? Message { get; set; }
    public bool IsCompleted { get; set; }
}