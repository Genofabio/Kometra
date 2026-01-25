using OpenCvSharp;
using System;
using System.Threading.Tasks;

namespace KomaLab.Services.Processing.Engines;

public interface IStructureExtractionEngine
{
    /// <summary>
    /// Applica il filtro Larson-Sekanina (Gradiente Rotazionale) in background.
    /// </summary>
    /// <param name="src">Immagine sorgente.</param>
    /// <param name="dst">Immagine destinazione.</param>
    /// <param name="angleDeg">Angolo di rotazione in gradi.</param>
    /// <param name="radialShiftX">Spostamento radiale X (pixel).</param>
    /// <param name="radialShiftY">Spostamento radiale Y (pixel).</param>
    /// <param name="isSymmetric">
    /// Se false: Standard ($I - Rot$). 
    /// Se true: Simmetrico ($2 \cdot I - Rot(+) - Rot(-)$).
    /// </param>
    Task ApplyLarsonSekaninaAsync(Mat src, Mat dst, double angleDeg, double radialShiftX, double radialShiftY, bool isSymmetric);

    /// <summary>
    /// Applica un filtro High-Pass sottraendo il background stimato tramite mediana.
    /// Gestisce kernel grandi (>5) su immagini float/double in parallelo senza bloccare la UI.
    /// </summary>
    /// <param name="src">Immagine sorgente.</param>
    /// <param name="dst">Immagine destinazione.</param>
    /// <param name="kernelSize">Dimensione del kernel mediana (deve essere dispari).</param>
    /// <param name="progress">Oggetto opzionale per monitorare la percentuale di completamento (0-100).</param>
    Task ApplyUnsharpMaskingMedianAsync(Mat src, Mat dst, int kernelSize, IProgress<double> progress = null);

    /// <summary>
    /// Applica il filtro RVSF (Radial Variable Slope Filter) adattivo in parallelo.
    /// Formula raggio kernel: $R = A + B \cdot \rho^N$
    /// </summary>
    /// <param name="src">Immagine sorgente.</param>
    /// <param name="dst">Immagine destinazione.</param>
    /// <param name="paramA">Termine costante (offset raggio).</param>
    /// <param name="paramB">Termine lineare (scala).</param>
    /// <param name="paramN">Termine esponenziale (potenza di rho).</param>
    /// <param name="useLog">Se true, converte l'immagine in $Log_{10}$ prima del calcolo.</param>
    /// <param name="progress">Oggetto opzionale per monitorare il progresso (0-100).</param>
    Task ApplyAdaptiveRVSFAsync(Mat src, Mat dst, double paramA, double paramB, double paramN, bool useLog, IProgress<double> progress = null);

    /// <summary>
    /// Genera un Mosaico (4x2) applicando l'RVSF 8 volte in parallelo.
    /// Utilizza semafori interni per evitare la saturazione della memoria RAM.
    /// </summary>
    /// <param name="src">Immagine sorgente.</param>
    /// <param name="dst">Immagine destinazione (Grande mosaico).</param>
    /// <param name="paramA">Coppia di valori per il parametro A.</param>
    /// <param name="paramB">Coppia di valori per il parametro B.</param>
    /// <param name="paramN">Coppia di valori per il parametro N.</param>
    /// <param name="useLog">Se true, converte l'immagine in $Log_{10}$.</param>
    Task ApplyRVSFMosaicAsync(Mat src, Mat dst, 
                              (double v1, double v2) paramA, 
                              (double v1, double v2) paramB, 
                              (double v1, double v2) paramN, 
                              bool useLog);
}