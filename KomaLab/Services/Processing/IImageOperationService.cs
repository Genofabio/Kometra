using System.Collections.Generic;
using System.Threading.Tasks;
using KomaLab.Models.Primitives;
using KomaLab.Models.Processing;
using OpenCvSharp;

namespace KomaLab.Services.Processing;

// ---------------------------------------------------------------------------
// FILE: IImageOperationService.cs
// RUOLO: Manipolatore di Immagini (Low-Level / Write)
// DESCRIZIONE:
// Esegue operazioni "distruttive" o trasformative sui dati pixel (Matrici).
// Totalmente disaccoppiato dal formato FITS.
// ---------------------------------------------------------------------------

public interface IImageOperationService
{
    // --- Allineamento e Warping ---

    /// <summary>
    /// Esegue il WarpAffine per traslare un'immagine con precisione sub-pixel, 
    /// centrandola su un nuovo punto. Il canvas risultante avrà dimensioni 'outputSize'.
    /// Le aree fuori dall'immagine originale vengono riempite con NaN (trasparenza).
    /// </summary>
    Mat GetSubPixelCenteredCanvas(Mat source, Point2D originalCenter, Size2D outputSize);

    // --- Template Matching ---

    /// <summary>
    /// Estrae un crop (template) attorno a una posizione stimata nell'immagine sorgente,
    /// raffinando il centro usando l'analisi gaussiana locale.
    /// </summary>
    (Mat Template, Point2D RefinedCenter) ExtractRefinedTemplate(Mat sourceImage, Point2D roughGuess, int radius);

    /// <summary>
    /// Cerca il template all'interno dell'immagine target, limitando la ricerca 
    /// a un raggio specifico attorno al centro atteso.
    /// </summary>
    Point2D? FindTemplatePosition(Mat searchImage, Mat template, Point2D expectedCenter, int searchRadius);

    // --- Stacking ---

    /// <summary>
    /// Esegue lo stacking (integrazione matematica) di una lista di matrici OpenCV.
    /// Restituisce la matrice risultante (Double).
    /// </summary>
    Task<Mat> ComputeStackAsync(List<Mat> sources, StackingMode mode);
}