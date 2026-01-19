using KomaLab.Models.Fits.Structure;

public class PlateSolvingResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public FitsHeader? SolvedHeader { get; set; }
    public string FullLog { get; set; } = "";
    
}