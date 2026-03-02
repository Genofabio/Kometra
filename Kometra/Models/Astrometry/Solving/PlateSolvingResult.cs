using Kometra.Models.Fits.Structure;

namespace Kometra.Models.Astrometry.Solving;

public class PlateSolvingResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public FitsHeader? SolvedHeader { get; set; }
    public string FullLog { get; set; } = "";
    
}