using KomaLab.Models.Visualization;
using OpenCvSharp;

namespace KomaLab.Services.Processing.Engines;

// ---------------------------------------------------------------------------------------
// DOMINIO: EFFETTI VISIVI E POINT OPERATIONS
// RESPONSABILITÀ: Trasformazione distruttiva dei valori dei pixel per scopi visuali.
// TIPI DI METODI: 
// - Quantizzazione (Posterizzazione).
// - Filtri di esaltazione (Larson-Sekanina, Sharpness, Morfologia).
// - Inversione e mappatura del contrasto (Look-up Tables).
// - Pesatura spaziale (Vignettatura sintetica).
// NOTA: Cambia il "look" dell'immagine alterando i valori dei singoli pixel (Asse Z).
// ---------------------------------------------------------------------------------------
public interface IImageEffectsEngine
{
    /// <summary>
    /// Riduce il numero di livelli tonali dell'immagine (Posterizzazione).
    /// </summary>
    void ApplyPosterization(Mat src, Mat dst, int levels, VisualizationMode mode, double bp, double wp);

    /// <summary>
    /// Applica una vignettatura sintetica (Gaussiana inversa) per dare peso maggiore 
    /// ai pixel centrali e oscurare i bordi.
    /// Utile per focalizzare l'analisi sul centro o per scopi estetici.
    /// </summary>
    /// <param name="src">Immagine sorgente.</param>
    /// <param name="dst">Immagine di destinazione (può essere la stessa di src).</param>
    /// <param name="sigmaScale">Fattore di scala per la larghezza della campana gaussiana (default 0.4).</param>
    void ApplyCentralWeighting(Mat src, Mat dst, double sigmaScale = 0.4);

    /// <summary>
    /// Applica un'operazione morfologica (Opening) per rimuovere piccoli artefatti luminosi
    /// (come stelle puntiformi o hot pixel), preservando strutture diffuse più grandi.
    /// </summary>
    /// <param name="src">Immagine sorgente.</param>
    /// <param name="dst">Immagine di destinazione.</param>
    /// <param name="kernelSize">Dimensione del kernel strutturante (default 3).</param>
    void ApplyMorphologicalCleanup(Mat src, Mat dst, int kernelSize = 3);
}