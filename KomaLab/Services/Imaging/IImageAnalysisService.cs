using OpenCvSharp;
using Point = Avalonia.Point;
using Rect = Avalonia.Rect;

namespace KomaLab.Services.Imaging;

public interface IImageAnalysisService
{
    /// <summary>
    /// Calcola Media e Deviazione Standard ignorando i NaN.
    /// Fondamentale per il Sigma Clipping e l'Auto-Stretch.
    /// </summary>
    (double Mean, double StdDev) ComputeStatistics(Mat image);

    /// <summary>
    /// Workflow completo: Analizza una regione (crop), trova la stella/cometa più luminosa 
    /// (Blob Detection) e ne calcola il centro preciso (Gaussian Fit).
    /// </summary>
    Point FindCenterOfLocalRegion(Mat region);

    // --- Algoritmi Core (Low Level) ---

    /// <summary>
    /// Calcola il baricentro (Image Moments).
    /// Ottimo per oggetti diffusi o molto sfocati.
    /// </summary>
    Point FindCentroid(Mat image, double sigma = 5.0);

    /// <summary>
    /// Trova il pixel più luminoso e applica un fit parabolico 3x3 per il sub-pixel.
    /// Veloce e stabile per stelle puntiformi.
    /// </summary>
    Point FindPeak(Mat image, double sigma = 1.0);

    /// <summary>
    /// Esegue un Fit Gaussiano 2D (MathNet Levenberg-Marquardt).
    /// Il metodo più accurato scientificamente, ma più lento.
    /// </summary>
    Point FindGaussianCenter(Mat image, double sigma = 3.0);
    
    /// <summary>
    /// Restituisce il rettangolo contenente dati validi (non-NaN).
    /// </summary>
    Rect FindValidDataBox(Mat image);
}