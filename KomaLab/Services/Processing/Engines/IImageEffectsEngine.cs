using KomaLab.Models.Visualization;
using OpenCvSharp;

namespace KomaLab.Services.Processing.Engines;

// ---------------------------------------------------------------------------------------
// DOMINIO: EFFETTI VISIVI E POINT OPERATIONS
// RESPONSABILITÀ: Trasformazione distruttiva dei valori dei pixel per scopi visuali.
// TIPI DI METODI: 
// - Quantizzazione (Posterizzazione).
// - Filtri di esaltazione (Larson-Sekanina, Sharpness).
// - Inversione e mappatura del contrasto (Look-up Tables).
// NOTA: Cambia il "look" dell'immagine alterando i valori dei singoli pixel (Asse Z).
// ---------------------------------------------------------------------------------------
public interface IImageEffectsEngine
{
    void ApplyPosterization(Mat src, Mat dst, int levels, VisualizationMode mode, double bp, double wp);
}