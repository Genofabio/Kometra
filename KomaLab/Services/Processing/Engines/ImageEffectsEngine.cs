using KomaLab.Models.Visualization;
using OpenCvSharp;

namespace KomaLab.Services.Processing.Engines;

/// <summary>
/// Implementazione dell'Alchimista dei Pixel.
/// Contiene la logica matematica per filtri ed effetti di intensità.
/// </summary>
public class ImageEffectsEngine : IImageEffectsEngine
{
    public ImageEffectsEngine() { }

    // =======================================================================
    // 1. EFFETTI DI QUANTIZZAZIONE (POSTERIZZAZIONE)
    // =======================================================================

    public void ApplyPosterization(Mat src, Mat dst, int levels, VisualizationMode mode, double bp, double wp)
    {
        // Protezione contro range nullo o inversione dei punti
        double range = wp - bp;
        if (range <= 1e-9) range = 1e-5; // Evita divisione per zero

        // Usiamo un Mat temporaneo a 32-bit Float per i calcoli intermedi di precisione
        using Mat tempFloat = new Mat();

        // 1. Normalizzazione e Stretching [Black..White] -> [0.0 .. 1.0]
        // La formula di conversione lineare è: output = input * alpha + beta
        // alpha = 1.0 / range
        // beta = -bp / range
        double scale = 1.0 / range;
        double offset = -bp * scale;

        src.ConvertTo(tempFloat, MatType.CV_32FC1, scale, offset);

        // Clamping rigoroso per assicurare che i valori siano esattamente tra 0.0 e 1.0
        Cv2.Max(tempFloat, 0.0, tempFloat);
        Cv2.Min(tempFloat, 1.0, tempFloat);

        // 2. Applicazione Curva di Trasferimento (Non-lineare)
        ApplyTransferCurve(tempFloat, mode);

        // 3. Quantizzazione (Il cuore dell'effetto Posterizzazione)
        
        // A. Scaliamo l'intervallo [0..1] in [0 .. levels-epsilon]
        // Usiamo levels-0.001 per evitare che il valore 1.0 esatto diventi 'levels' dopo il floor
        Cv2.Multiply(tempFloat, (double)levels - 0.001, tempFloat);

        // B. Troncamento a intero (Floor) tramite conversione a Int32
        using Mat tempInt = new Mat();
        tempFloat.ConvertTo(tempInt, MatType.CV_32SC1);
        
        // C. Ritorno a Float32 per la rinormalizzazione
        tempInt.ConvertTo(tempFloat, MatType.CV_32FC1);

        // D. Riscaliamo nell'intervallo [0..1] normalizzato
        double divScale = levels > 1 ? 1.0 / (levels - 1) : 1.0;
        Cv2.Multiply(tempFloat, divScale, tempFloat);

        // 4. Conversione finale a 8-bit [0..255] per la destinazione (Output Visualizzabile)
        tempFloat.ConvertTo(dst, MatType.CV_8UC1, 255.0);
    }

    // =======================================================================
    // HELPERS PRIVATI
    // =======================================================================

    /// <summary>
    /// Applica la curva di trasferimento non lineare (Gamma) all'immagine normalizzata.
    /// </summary>
    private void ApplyTransferCurve(Mat imageNormalized, VisualizationMode mode)
    {
        switch (mode)
        {
            case VisualizationMode.SquareRoot:
                // Utile per comprimere le alte luci
                Cv2.Sqrt(imageNormalized, imageNormalized);
                break;
            case VisualizationMode.Logarithmic:
                // Utile per esaltare le ombre profonde (es. nebulose deboli)
                // Approssimazione di log2(x + 1): Math.Log(x+1) / Math.Log(2)
                // Costante 1 / ln(2) ~= 1.442695
                Cv2.Add(imageNormalized, 1.0, imageNormalized); // Evita log(0), sposta range a [1..2]
                Cv2.Log(imageNormalized, imageNormalized);
                Cv2.Multiply(imageNormalized, 1.442695, imageNormalized); // Riporta a [0..1]
                break;
            case VisualizationMode.Linear:
            default:
                // Nessuna operazione necessaria, l'immagine è già lineare [0..1]
                break;
        }
    }
}