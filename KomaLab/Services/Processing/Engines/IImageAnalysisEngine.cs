using KomaLab.Models.Primitives;
using OpenCvSharp;

namespace KomaLab.Services.Processing.Engines;

// ---------------------------------------------------------------------------------------
// DOMINIO: ANALISI SPAZIALE (ASTROMETRIA E DISCOVERY)
// RESPONSABILITÀ: Individuazione di coordinate, pattern e confini (Assi X, Y).
// TIPI DI METODI: 
// - Centroiding e Peak-finding (Gaussiana, PSF).
// - Riconoscimento di pattern (Template Matching).
// - Discovery dei confini del segnale (Valid Data Box).
// - Calcolo di shift e offset tra immagini.
// NOTA: Restituisce solo coordinate o metadati, non crea nuove immagini.
// ---------------------------------------------------------------------------------------

public interface IImageAnalysisEngine
{
    // --- Discovery & Bounding ---
    Rect2D FindValidDataBox(Mat image);
    
    // --- Registrazione (Shift & Template) ---
    (Point2D Shift, double Confidence) ComputeStarFieldShift(Mat reference, Mat target);
    Point2D? FindTemplatePosition(Mat searchImage, Mat template, Point2D expectedCenter, int searchRadius);
    
    // --- Centroiding (Precisione Sub-pixel) ---
    Point2D FindCenterOfLocalRegion(Mat region);
    Point2D FindGaussianCenter(Mat image, double sigma = 3.0);
    Point2D FindPeak(Mat image, double sigma = 1.0);
    Point2D FindCentroid(Mat image, double sigma = 5.0);
    Point2D FindAsymmetricQuadrantCenter(Mat region);
}