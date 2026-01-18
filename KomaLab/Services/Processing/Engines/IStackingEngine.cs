using System.Collections.Generic;
using System.Threading.Tasks;
using KomaLab.Models.Processing;
using OpenCvSharp;

namespace KomaLab.Services.Processing.Engines;

// ---------------------------------------------------------------------------------------
// DOMINIO: INTEGRAZIONE DATI (STACKING)
// RESPONSABILITÀ: Fusione matematica di N immagini in un unico risultato.
// TIPI DI METODI: 
// - Algoritmi statistici di integrazione (Media, Mediana, Somma).
// - Gestione di set di dati massivi in parallelo.
// NOTA: Opera puramente su matrici in memoria per produrre un segnale sintetico pulito.
// ---------------------------------------------------------------------------------------

public interface IStackingEngine
{
    /// <summary>
    /// Esegue la fusione matematica di una lista di matrici.
    /// </summary>
    /// <param name="sources">Lista di matrici (devono avere le stesse dimensioni).</param>
    /// <param name="mode">Algoritmo di integrazione (Sum, Average, Median).</param>
    /// <returns>Una nuova matrice contenente il risultato dello stacking.</returns>
    Task<Mat> ComputeStackAsync(List<Mat> sources, StackingMode mode);
}