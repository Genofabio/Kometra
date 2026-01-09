using System.Collections.Generic;
using System.Threading.Tasks;
using KomaLab.Models;
using KomaLab.Models.Fits;
using KomaLab.Models.Processing;
using OpenCvSharp;
using Point = Avalonia.Point;
using Size = Avalonia.Size;

namespace KomaLab.Services.Imaging;

public interface IImageOperationService
{
    // --- Allineamento e Warping ---

    /// <summary>
    /// Esegue il WarpAffine per centrare un'immagine su un punto specifico.
    /// Crea un nuovo canvas inizializzato a NaN (trasparente).
    /// </summary>
    Mat GetSubPixelCenteredCanvas(Mat source, Point originalCenter, Size outputSize);

    // --- Template Matching ---

    /// <summary>
    /// Estrae un crop (template) attorno a una posizione stimata, 
    /// raffinando il centro usando l'analisi gaussiana.
    /// </summary>
    (Mat template, Point preciseCenter) ExtractRefinedTemplate(FitsImageData? data, Point roughGuess, int radius);

    /// <summary>
    /// Cerca il template nell'immagine target dentro un raggio di ricerca.
    /// </summary>
    Point? FindTemplatePosition(Mat searchImage, Mat template, Point expectedCenter, int searchRadius);

    // --- Stacking ---

    /// <summary>
    /// Esegue lo stacking (Somma, Media, Mediana) di una lista di immagini.
    /// Usa IFitsDataConverter per caricare/salvare i dati.
    /// </summary>
    Task<FitsImageData> ComputeStackAsync(List<FitsImageData> sources, StackingMode mode);
}