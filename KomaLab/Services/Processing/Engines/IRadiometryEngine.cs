using KomaLab.Models.Visualization;
using OpenCvSharp;

namespace KomaLab.Services.Processing.Engines;

// ---------------------------------------------------------------------------------------
// DOMINIO: RADIOMETRIA (INTENSITÀ DEL SEGNALE)
// RESPONSABILITÀ: Analisi non distruttiva dei valori dei pixel (Asse Z).
// TIPI DI METODI: 
// - Calcolo di statistiche (Media, Sigma).
// - Analisi dell'istogramma (Quantili, AutoStretch).
// - Trasformazioni matematiche di range (ADU <-> Sigma).
// NOTA: Non gli interessa DOVE sono i pixel, ma solo che VALORE hanno.
// ---------------------------------------------------------------------------------------
public interface IRadiometryEngine
{
    /// <summary>
    /// Calcola Media e Deviazione Standard ignorando i valori NaN (O(N)).
    /// </summary>
    (double Mean, double StdDev) ComputeStatistics(Mat image);

    /// <summary>
    /// Estrae un array di campioni pixel per analisi statistiche veloci.
    /// </summary>
    double[] GetPixelSamples(Mat image, int maxSamples = 10000);

    /// <summary>
    /// Calcola i livelli di soglia ideali basati sui quantili dell'intera immagine.
    /// </summary>
    AbsoluteContrastProfile CalculateAutoStretchProfile(Mat image);

    /// <summary>
    /// Calcola un profilo di contrasto basato sui quantili da un array di campioni pre-esistente.
    /// </summary>
    AbsoluteContrastProfile CalculateAutoStretchFromSamples(double[] samples);

    /// <summary>
    /// Calcola un profilo di contrasto basato su Media e Sigma (Metodo deviazione standard).
    /// </summary>
    AbsoluteContrastProfile CalculateAutoStretchFromStats(double mean, double stdDev);

    /// <summary>
    /// Calcola l'adattamento di contrasto tra due immagini cercando di mantenere la stessa percezione visiva.
    /// </summary>
    AbsoluteContrastProfile CalculateAdaptedProfile(Mat sourceMat, Mat targetMat, double cb, double cw, (double Mean, double StdDev)? ss = null, (double Mean, double StdDev)? ts = null);

    /// <summary>
    /// Converte valori ADU assoluti in Z-Score (Sigma) relativi alla distribuzione dell'immagine.
    /// </summary>
    SigmaContrastProfile ComputeSigmaProfile(Mat? image, double cb, double cw, (double Mean, double StdDev)? stats = null);

    /// <summary>
    /// Converte Z-Score (Sigma) in valori assoluti ADU basandosi sulla distribuzione dell'immagine corrente.
    /// </summary>
    AbsoluteContrastProfile ComputeAbsoluteFromSigma(Mat? image, SigmaContrastProfile profile, (double Mean, double StdDev)? stats = null);
}