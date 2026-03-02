using Kometra.Models.Visualization;
using OpenCvSharp;

namespace Kometra.Services.Processing.Engines;

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
    
    /// <summary>
    /// Applica una vignettatura sintetica per dare peso maggiore al centro dell'immagine.
    /// Fondamentale per ignorare stelle ai bordi durante il tracking di comete.
    /// </summary>
    /// <param name="sigmaScale">Quanto è "larga" la zona centrale (0.4 è standard)</param>
    public void ApplyCentralWeighting(Mat src, Mat dst, double sigmaScale = 0.4)
    {
        if (src.Empty()) return;

        // Assicuriamo che dst abbia la dimensione giusta
        if (dst.Size() != src.Size() || dst.Type() != src.Type())
            src.CopyTo(dst);

        int w = src.Cols;
        int h = src.Rows;

        // 1. Generiamo i kernel gaussiani 1D
        // Usiamo CV_32F o CV_64F per la maschera
        using Mat gX = Cv2.GetGaussianKernel(w, w * sigmaScale, MatType.CV_32FC1);
        using Mat gY = Cv2.GetGaussianKernel(h, h * sigmaScale, MatType.CV_32FC1);
        
        // 2. Creiamo la maschera 2D (Outer Product: M = gY * gX')
        using Mat gXt = new Mat();
        Cv2.Transpose(gX, gXt);
        
        // Nota: "*" tra Mat in OpenCvSharp è moltiplicazione matriciale (quello che vogliamo qui per creare la griglia)
        using Mat mask = gY * gXt; 

        // 3. Normalizziamo la maschera [0..1] per sicurezza
        Cv2.Normalize(mask, mask, 0, 1, NormTypes.MinMax);

        // 4. Moltiplicazione Element-Wise (Pixel per Pixel)
        // Dobbiamo gestire i tipi: se src è ushort, dobbiamo convertire la maschera o src
        if (src.Depth() == MatType.CV_8U || src.Depth() == MatType.CV_16U)
        {
            // Convertiamo src a float temporaneamente per la moltiplicazione precisa
            using Mat srcFloat = new Mat();
            src.ConvertTo(srcFloat, MatType.CV_32FC1);
            
            Cv2.Multiply(srcFloat, mask, srcFloat);
            
            // Torniamo al tipo originale in dst
            srcFloat.ConvertTo(dst, src.Type());
        }
        else
        {
            // Se è già float/double, moltiplichiamo diretto
            // Assicuriamoci che la maschera abbia lo stesso tipo esatto
            using Mat maskConverted = new Mat();
            mask.ConvertTo(maskConverted, src.Type());
            Cv2.Multiply(src, maskConverted, dst);
        }
    }

    /// <summary>
    /// Applica un'apertura morfologica per rimuovere stelle puntiformi e hot pixel,
    /// lasciando intatta la struttura diffusa della cometa.
    /// </summary>
    public void ApplyMorphologicalCleanup(Mat src, Mat dst, int kernelSize = 3)
    {
        using Mat kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(kernelSize, kernelSize));
        // MorphTypes.Open = Erosione seguita da Dilatazione (Rimuove piccoli oggetti luminosi)
        Cv2.MorphologyEx(src, dst, MorphTypes.Open, kernel);
    }
}