namespace Kometra.Models.Processing.Masking;

public class MaskingParameters
{
    // --- Parametri Cometa ---
    public double CometThresholdSigma { get; set; } = 2.0;
    
    public int CometDilation { get; set; } = 0; 

    // --- Parametri Stelle ---
    public double StarThresholdSigma { get; set; } = 2.0;
    
    public int StarDilation { get; set; } = 0; 
    
    // [NUOVO] Diametro minimo per considerare un oggetto come stella e non rumore.
    // Default 3 (rimuove pixel singoli 1x1 o 2x2).
    // Impostare a 1 per disattivare il filtro (per ottiche grandangolari).
    public int MinStarDiameter { get; set; } = 3;
}