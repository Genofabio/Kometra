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
    /// Calcola il centro di massa (baricentro).
    /// </summary>
    Centroid,
    
    /// <summary>
    /// Trova il picco sub-pixel (fit parabolico 3x3).
    /// </summary>
    Peak,
    
    /// <summary>
    /// Esegue un fit Gaussiano 2D sulla regione.
    /// </summary>
    GaussianFit
}

/// <summary>
/// Definisce il contratto per tutti i calcoli
/// di image processing (OpenCV e Math.NET).
/// </summary>
public interface IImageProcessingService
{
    // --- Metodi di Conversione Dati ---
    Mat LoadFitsDataAsMat(FitsImageData fitsData);
    
    /// <summary>
    /// Calcola le soglie di visualizzazione (percentili) per un set di dati FITS.
    /// </summary>
    (double BlackPoint, double WhitePoint) CalculateClippedThresholds(FitsImageData fitsData);

    /// <summary>
    /// Converte una Mat di OpenCV (dati processati) di nuovo in un FitsImageData.
    /// </summary>
    FitsImageData CreateFitsDataFromMat(Mat mat, FitsImageData originalData);

    // --- Metodi di Workflow ---
    Point GetCenterOfLocalRegion(
        FitsImageData fitsData,
        CenteringMethod centerFunc,
        double thresholdRatio = 0.1,
        int minArea = 10,
        int padding = 0);
    
    Mat CenterImageByCoords(Mat imageMat, Point centerPoint);

    // --- Metodi Primitivi di Centraggio ---
    Point GetCenterByCentroid(Mat imageMat, double sigma = 5.0);
    Point GetCenterByPeak(Mat imageMat, double sigma = 1.0);
    Point GetCenterByGaussianFit(Mat imageMat, double thresholdRatio = 0.5, double sigma = 3.0);
}