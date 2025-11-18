using System.Collections.Generic;
using System.Threading.Tasks;
using KomaLab.Models;
using OpenCvSharp;
using Point = Avalonia.Point;

namespace KomaLab.Services;

/// <summary>
/// Definisce i metodi di centraggio disponibili.
/// </summary>
public enum CenteringMethod
{
    /// <summary>
    /// Calcola il centro di massa (baricentro). Richiede forte sfocatura.
    /// </summary>
    Centroid,
    
    /// <summary>
    /// Trova il picco sub-pixel (fit parabolico/baricentro 3x3). Richiede poca sfocatura.
    /// </summary>
    Peak,
    
    /// <summary>
    /// Esegue un fit Gaussiano 2D sulla regione. Sfocatura media.
    /// </summary>
    GaussianFit,
    
    /// <summary>
    /// Metodo standard "Plate Solving Locale": 
    /// Esegue Blob Detection automatica + Fit Gaussiano.
    /// </summary>
    LocalRegion
}

/// <summary>
/// Definisce il contratto per tutti i calcoli
/// di image processing (OpenCV e Math.NET).
/// </summary>
public interface IImageProcessingService
{
    // --- 1. FITS I/O & Data Bridge ---
    // Responsabilità: Convertire da/a modelli di dati FITS.
    
    /// <summary>
    /// Converte i dati grezzi FITS in una Matrice OpenCV.
    /// </summary>
    Mat LoadFitsDataAsMat(FitsImageData fitsData);
    
    /// <summary>
    /// Converte una Mat di OpenCV (dati processati) di nuovo in un FitsImageData.
    /// </summary>
    FitsImageData CreateFitsDataFromMat(Mat mat, FitsImageData originalData);
    
    /// <summary>
    /// Calcola le soglie di visualizzazione (percentili) per un set di dati FITS.
    /// </summary>
    (double BlackPoint, double WhitePoint) CalculateClippedThresholds(FitsImageData fitsData);

    // --- 2. High-Level Centering Workflows ---
    // Responsabilità: Eseguire logiche complesse (multi-step) per trovare un centro.
    
    /// <summary>
    /// Metodo "Intelligente": riceve un crop, isola la stella più grande (Blob Detection)
    /// e calcola il centro usando GaussianFit.
    /// </summary>
    Point GetCenterOfLocalRegion(Mat regionMat);

    // --- 3. Core Centering Algorithms (Math) ---
    // Responsabilità: Data un'immagine (già preparata), calcolare un centro.
    
    /// <summary>
    /// Calcola il centro di massa (baricentro) dopo un forte blur (sigma 5.0).
    /// </summary>
    Point GetCenterByCentroid(Mat rawMat, double sigma = 5.0);
    
    /// <summary>
    /// Trova il picco sub-pixel dopo un leggero blur (sigma 1.0).
    /// </summary>
    Point GetCenterByPeak(Mat rawMat, double sigma = 1.0);
    
    /// <summary>
    /// Esegue un fit Gaussiano 2D sulla regione dopo un blur medio (sigma 3.0).
    /// </summary>
    Point GetCenterByGaussianFit(Mat rawMat, double sigma = 3.0);

    // --- 4. Geometric Transformations & Utilities ---
    // Responsabilità: Manipolare o analizzare la geometria della Mat.
    
    /// <summary>
    /// Esegue il WarpAffine per centrare un'immagine su un punto,
    /// creando un nuovo canvas e riempiendo i bordi con NaN.
    /// </summary>
    Mat GetSubPixelCenteredCanvas(Mat source, Point originalCenter, Size outputSize);

    /// <summary>
    /// Trova il rettangolo dei dati validi (escludendo bordi NaN).
    /// </summary>
    Rect FindValidDataBox(Mat imageMat);
    
    (Mat template, Point preciseCenter) ExtractRefinedTemplate(FitsImageData? data, Point roughGuess, int radius);
    Point? FindTemplatePosition(Mat searchImage, Mat template, Point expectedCenter, int searchRadius);
    
    /// <summary>
    /// Esegue lo stacking (Somma, Media, Mediana) di una lista di immagini.
    /// Ritorna una nuova struttura dati FITS (sempre in Double precision).
    /// </summary>
    Task<FitsImageData> ComputeStackAsync(List<FitsImageData> sources, StackingMode mode);
}