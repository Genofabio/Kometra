using KomaLab.Models.Primitives;
using KomaLab.Models.Visualization;
using OpenCvSharp;

namespace KomaLab.Services.Processing;

// ---------------------------------------------------------------------------
// FILE: IImageAnalysisService.cs
// RUOLO: Analizzatore Matematico (Low-Level / Read-Only)
// DESCRIZIONE:
// Motore di calcolo puro che estrae metadati numerici dalle matrici.
// Totalmente disaccoppiato dal formato FITS.
// ---------------------------------------------------------------------------

public interface IImageAnalysisService
{
    // --- Statistiche Base ---
    
    /// <summary>
    /// Calcola Media e Deviazione Standard dell'immagine ignorando i valori NaN.
    /// </summary>
    (double Mean, double StdDev) ComputeStatistics(Mat image);

    // --- Gestione Contrasto (Strategia Sigma/Z-Score) ---
    
    /// <summary>
    /// Calcola i livelli ideali (AutoStretch) per un'immagine grezza.
    /// </summary>
    AbsoluteContrastProfile CalculateAutoStretchProfile(Mat image);
    
    /// <summary>
    /// Calcola il nuovo profilo di contrasto assoluto per l'immagine target,
    /// cercando di mantenere la percezione visiva dell'immagine sorgente.
    /// </summary>
    AbsoluteContrastProfile CalculateAdaptedProfile(
        Mat sourceMat, 
        Mat targetMat, 
        double currentBlack, 
        double currentWhite);

    /// <summary>
    /// Versione leggera: calcola l'adattamento basandosi solo sulle statistiche pre-calcolate.
    /// Non richiede accesso alle Matrici pixel (Mat).
    /// </summary>
    AbsoluteContrastProfile CalculateAdaptedProfileFromStats(
        (double Mean, double StdDev) sourceStats,
        double sourceBlack,
        double sourceWhite,
        (double Mean, double StdDev) targetStats);

    /// <summary>
    /// Calcola un profilo SIGMA (Z-Score) basato sulla distanza statistica
    /// tra i valori correnti e la media dell'immagine.
    /// </summary>
    SigmaContrastProfile ComputeSigmaProfile(Mat image, double currentBlack, double currentWhite);

    /// <summary>
    /// Calcola i valori ASSOLUTI per una nuova immagine applicando lo stesso Z-Score
    /// del profilo fornito.
    /// </summary>
    AbsoluteContrastProfile ComputeAbsoluteFromSigma(Mat image, SigmaContrastProfile profile);

    // --- Astrometria & Centroiding ---
    
    /// <summary>
    /// Workflow completo di centratura locale su regione.
    /// </summary>
    Point2D FindCenterOfLocalRegion(Mat region);

    /// <summary>
    /// Calcola il baricentro (Image Moments).
    /// </summary>
    Point2D FindCentroid(Mat image, double sigma = 5.0);

    /// <summary>
    /// Trova il pixel più luminoso (Peak) con fit parabolico.
    /// </summary>
    Point2D FindPeak(Mat image, double sigma = 1.0);

    /// <summary>
    /// Esegue un Fit Gaussiano 2D (metodo scientifico accurato).
    /// </summary>
    Point2D FindGaussianCenter(Mat image, double sigma = 3.0);
    
    /// <summary>
    /// Restituisce il rettangolo (Bounding Box) che contiene dati validi (non NaN).
    /// </summary>
    Rect2D FindValidDataBox(Mat image);
    
    // --- Analisi Campo Stellare (Allineamento) ---

    /// <summary>
    /// Calcola il vettore di spostamento (Shift X, Y) tra due immagini
    /// utilizzando la Phase Correlation (FFT).
    /// </summary>
    Point2D ComputeStarFieldShift(Mat reference, Mat target);
}