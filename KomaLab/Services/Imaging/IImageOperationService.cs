using System.Collections.Generic;
using System.Threading.Tasks;
using KomaLab.Models.Fits;
using KomaLab.Models.Primitives;
using KomaLab.Models.Processing;
using OpenCvSharp;

namespace KomaLab.Services.Imaging;

// ---------------------------------------------------------------------------
// FILE: IImageOperationService.cs
// RUOLO: Manipolatore di Immagini (Low-Level / Write)
// DESCRIZIONE:
// Esegue operazioni "distruttive" o trasformative sui dati grezzi.
// A differenza dell'analisi, questo servizio genera NUOVE immagini o modifica i pixel.
// Responsabilità:
// - Generazione di Canvas traslati (Warping geometrico).
// - Creazione di Crop/Template (estrazione sottomatrici).
// - Fusione di immagini (Stacking matematico: Somma/Media/Mediana).
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
    /// Estrae un crop (template) attorno a una posizione stimata, 
    /// raffinando il centro usando l'analisi gaussiana.
    /// Utile per creare il "modello" della cometa da cercare negli altri frame.
    /// </summary>
    (Mat Template, Point2D RefinedCenter) ExtractRefinedTemplate(FitsImageData? data, Point2D roughGuess, int radius);

    /// <summary>
    /// Cerca il template all'interno dell'immagine target, limitando la ricerca 
    /// a un raggio specifico attorno al centro atteso.
    /// </summary>
    Point2D? FindTemplatePosition(Mat searchImage, Mat template, Point2D expectedCenter, int searchRadius);

    // --- Stacking ---

    /// <summary>
    /// Esegue lo stacking (integrazione) di una lista di immagini già allineate.
    /// Supporta Somma, Media e Mediana (con gestione ottimizzata della memoria).
    /// </summary>
    Task<FitsImageData> ComputeStackAsync(List<FitsImageData> sources, StackingMode mode);
}