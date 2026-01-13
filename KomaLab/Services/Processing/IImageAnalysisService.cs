using KomaLab.Models.Fits;
using KomaLab.Models.Primitives;
using KomaLab.Models.Visualization;
using OpenCvSharp;

namespace KomaLab.Services.Processing;

// ---------------------------------------------------------------------------
// FILE: IImageAnalysisService.cs
// RUOLO: Analizzatore Matematico (Low-Level / Read-Only)
// DESCRIZIONE:
// Motore di calcolo puro che estrae metadati numerici dalle immagini.
// Implementa la strategia "Sigma-Clipping" per la gestione del contrasto.
// ---------------------------------------------------------------------------

public interface IImageAnalysisService
{
    // --- Statistiche Base ---
    
    /// <summary>
    /// Calcola Media e Deviazione Standard dell'immagine ignorando i valori NaN.
    /// Fondamentale per determinare soglie di rumore e clipping.
    /// </summary>
    (double Mean, double StdDev) ComputeStatistics(Mat image);

    // --- Gestione Contrasto (Strategia Sigma/Z-Score) ---
    
    /// <summary>
    /// Calcola i livelli ideali (AutoStretch) per un'immagine grezza.
    /// Utilizza il quantile 0.3 (30%) per il nero per garantire un fondo cielo scuro
    /// e il 0.995 per il bianco.
    /// </summary>
    AbsoluteContrastProfile CalculateAutoStretchProfile(Mat image);
    
    /// <summary>
    /// Calcola il nuovo profilo di contrasto assoluto per l'immagine target,
    /// cercando di mantenere la percezione visiva dell'immagine sorgente
    /// (basandosi sulla deviazione standard / Sigma).
    /// </summary>
    AbsoluteContrastProfile CalculateAdaptedProfile(
        FitsImageData sourceData, 
        FitsImageData targetData, 
        double currentBlack, 
        double currentWhite);

    /// <summary>
    /// Calcola un profilo SIGMA (Z-Score) basato sulla distanza statistica
    /// tra i valori correnti (impostati dall'utente) e la media dell'immagine.
    /// Esempio: "Il nero è a -1.5 Sigma dalla media".
    /// </summary>
    SigmaContrastProfile ComputeSigmaProfile(Mat image, double currentBlack, double currentWhite);

    /// <summary>
    /// Calcola i valori ASSOLUTI per una nuova immagine applicando lo stesso Z-Score
    /// (distanza dalla media in sigma) del profilo fornito.
    /// Questo adatta il contrasto al livello di rumore della nuova immagine.
    /// </summary>
    AbsoluteContrastProfile ComputeAbsoluteFromSigma(Mat image, SigmaContrastProfile profile);

    // --- Astrometria & Centroiding (Invariati) ---
    
    /// <summary>
    /// Workflow completo di centratura locale:
    /// 1. Analizza la regione fornita (crop).
    /// 2. Esegue una Blob Detection per trovare l'oggetto principale.
    /// 3. Raffina la posizione sub-pixel usando un Gaussian Fit o Momenti.
    /// </summary>
    Point2D FindCenterOfLocalRegion(Mat region);

    /// <summary>
    /// Calcola il baricentro (Image Moments) dell'intensità luminosa.
    /// Ottimo per oggetti diffusi, irregolari o molto sfocati (defocus).
    /// </summary>
    Point2D FindCentroid(Mat image, double sigma = 5.0);

    /// <summary>
    /// Trova il pixel più luminoso e applica un fit parabolico 3x3 per ottenere
    /// coordinate sub-pixel. Estremamente veloce e stabile per stelle puntiformi.
    /// </summary>
    Point2D FindPeak(Mat image, double sigma = 1.0);

    /// <summary>
    /// Esegue un Fit Gaussiano 2D (algoritmo Levenberg-Marquardt via MathNet).
    /// È il metodo scientificamente più accurato per le stelle (PSF Gaussiana).
    /// </summary>
    Point2D FindGaussianCenter(Mat image, double sigma = 3.0);
    
    /// <summary>
    /// Restituisce il rettangolo (Bounding Box) che contiene dati validi.
    /// Utile per escludere bordi neri o aree di padding (NaN/Zero) dopo un allineamento.
    /// </summary>
    Rect2D FindValidDataBox(Mat image);
    
    // --- Analisi Campo Stellare (Allineamento) ---

    /// <summary>
    /// Calcola il vettore di spostamento (Shift X, Y) tra due immagini stellari
    /// utilizzando la Phase Correlation (FFT).
    /// </summary>
    Point2D ComputeStarFieldShift(Mat reference, Mat target);
}