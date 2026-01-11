using KomaLab.Models.Primitives;
using OpenCvSharp;

namespace KomaLab.Services.Imaging;

// ---------------------------------------------------------------------------
// FILE: IImageAnalysisService.cs
// RUOLO: Analizzatore Matematico (Low-Level / Read-Only)
// DESCRIZIONE:
// Motore di calcolo puro che estrae metadati numerici dalle immagini.
// NON modifica mai i pixel in ingresso e NON genera nuove immagini.
// Responsabilità:
// - Misurazioni (Statistiche, SNR, Background).
// - Localizzazione (Centroidi, Peak Finding, Gaussian Fitting).
// - Correlazione (Calcolo vettori di spostamento FFT).
// ---------------------------------------------------------------------------

public interface IImageAnalysisService
{
    /// <summary>
    /// Calcola Media e Deviazione Standard dell'immagine ignorando i valori NaN.
    /// Fondamentale per determinare soglie di rumore e clipping.
    /// </summary>
    (double Mean, double StdDev) ComputeStatistics(Mat image);
    
    /// <summary>
    /// Calcola i livelli di Nero e Bianco ideali per l'Auto-Stretch (contrasto automatico).
    /// Utilizza un campionamento statistico (Quantili) per ignorare outlier e rumore.
    /// </summary>
    /// <param name="image">Immagine sorgente (preferibilmente Double Precision).</param>
    /// <returns>Valori di cut-off (Black, White).</returns>
    (double Black, double White) CalculateAutoStretchLevels(Mat image);

    /// <summary>
    /// Workflow completo di centratura locale:
    /// 1. Analizza la regione fornita (crop).
    /// 2. Esegue una Blob Detection per trovare l'oggetto principale.
    /// 3. Raffina la posizione sub-pixel usando un Gaussian Fit o Momenti.
    /// </summary>
    /// <param name="region">Matrice ritagliata attorno all'area di interesse.</param>
    /// <returns>Le coordinate del centro relative all'immagine originale (se region conserva l'offset) o locali.</returns>
    Point2D FindCenterOfLocalRegion(Mat region);

    // --- Algoritmi Core (Low Level) ---

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
    /// È il metodo scientificamente più accurato per le stelle (PSF Gaussiana),
    /// ma è computazionalmente più oneroso.
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
    /// Include pre-processing (Sobel/TopHat) per isolare le stelle dal fondo cielo.
    /// </summary>
    /// <param name="reference">Immagine di riferimento (solitamente la precedente).</param>
    /// <param name="target">Immagine da allineare.</param>
    /// <returns>Il vettore di spostamento (dx, dy).</returns>
    Point2D ComputeStarFieldShift(Mat reference, Mat target);
}