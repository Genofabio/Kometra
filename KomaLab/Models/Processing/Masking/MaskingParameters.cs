namespace KomaLab.Models.Processing.Masking;

public class MaskingParameters
{
    // --- Parametri Cometa ---
    public double CometThresholdSigma { get; set; } = 2.0;
    
    // Default 0 come richiesto
    public int CometDilation { get; set; } = 0; 

    // --- Parametri Stelle ---
    public double StarThresholdSigma { get; set; } = 2.0;
    
    // Default 0 come richiesto
    public int StarDilation { get; set; } = 0; 
    
}