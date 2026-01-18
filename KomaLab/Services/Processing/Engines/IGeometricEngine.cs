using KomaLab.Models.Primitives;
using OpenCvSharp;

namespace KomaLab.Services.Processing.Engines;

/// <summary>
/// Motore per trasformazioni geometriche e manipolazione spaziale dei pixel.
/// Opera esclusivamente su matrici OpenCV applicando calcoli matematici puri.
/// </summary>
public interface IGeometricEngine
{
    /// <summary>
    /// Applica una traslazione con precisione sub-pixel e ricampionamento Lanczos4.
    /// Sposta l'immagine sorgente in modo che il punto 'sourcePoint' coincida 
    /// con 'targetPoint' nel nuovo canvas.
    /// </summary>
    Mat WarpTranslation(Mat source, Point2D sourcePoint, Point2D targetPoint, Size2D outputSize);

    /// <summary>
    /// Estrae una regione di interesse (ROI) centrata su un punto, 
    /// applicando normalizzazione per analisi o pattern matching.
    /// </summary>
    Mat ExtractRegion(Mat source, Point2D center, int radius);
}