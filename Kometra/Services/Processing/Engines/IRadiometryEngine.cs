using Kometra.Models.Visualization;
using OpenCvSharp;

namespace Kometra.Services.Processing.Engines;

/// <summary>
/// RESPONSABILITÀ: Analisi non distruttiva dei valori dei pixel (Asse Z).
/// Gestisce statistiche, istogrammi e trasformazioni di range radiometrici.
/// Supporta matrici a 32-bit (CV_32F) e 64-bit (CV_64F).
/// </summary>
public interface IRadiometryEngine
{
    // =======================================================================
    // 1. ANALISI PIXEL (Operazioni Pesanti O(N))
    // =======================================================================

    /// <summary>
    /// Calcola Media e Deviazione Standard ignorando esplicitamente i valori NaN e Infiniti.
    /// </summary>
    (double Mean, double StdDev) ComputeStatistics(Mat image);

    public (double Mean, double StdDev, double MedianApprox) ComputeRobustStatistics(Mat image, double sigma = 3.0,
        int maxIterations = 5);

    /// <summary>
    /// Estrae un array di campioni pixel ignorando i NaN per analisi statistiche veloci.
    /// </summary>
    double[] GetPixelSamples(Mat image, int maxSamples = 10000);

    /// <summary>
    /// Analizza l'intero istogramma per calcolare i punti di nero e bianco (AutoStretch).
    /// </summary>
    AbsoluteContrastProfile CalculateAutoStretchProfile(Mat image);

    // =======================================================================
    // 2. ALGEBRA RADIOMETRICA (Operazioni Leggere O(1))
    // =======================================================================
    
    /// <summary>
    /// Converte valori ADU assoluti in Z-Score (Sigma) usando statistiche pre-calcolate.
    /// NIENTE PIÙ PASSAGGIO DI MATRICI: evita scansioni ridondanti.
    /// </summary>
    SigmaContrastProfile ComputeSigmaProfile(double blackAdu, double whiteAdu, (double Mean, double StdDev) stats);

    /// <summary>
    /// Converte Z-Score (Sigma) in valori ADU assoluti usando statistiche pre-calcolate.
    /// </summary>
    AbsoluteContrastProfile ComputeAbsoluteFromSigma(SigmaContrastProfile profile, (double Mean, double StdDev) stats);
}