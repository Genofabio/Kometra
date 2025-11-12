using Avalonia;
using KomaLab.ViewModels; // Per AlignmentMode
using System.Collections.Generic;
using System.Threading.Tasks;

namespace KomaLab.Services;

/// <summary>
/// Definisce il contratto per il servizio di calcolo dell'allineamento.
/// </summary>
public interface IAlignmentService
{
    /// <summary>
    /// Calcola i centri delle immagini in base alla modalità e alle coordinate fornite.
    /// </summary>
    /// <param name="mode">La modalità di allineamento (Automatic, Guided, Manual).</param>
    /// <param name="currentCoordinates">L'elenco delle coordinate correnti.</param>
    /// <param name="imageSize">La dimensione dell'immagine (necessaria per il calcolo automatico).</param>
    /// <returns>Un nuovo elenco di coordinate calcolate.</returns>
    Task<IEnumerable<Point?>> CalculateCentersAsync(
        AlignmentMode mode, 
        IEnumerable<Point?> currentCoordinates, 
        Size imageSize);

    /// <summary>
    /// Determina se il calcolo può essere eseguito in base allo stato corrente.
    /// </summary>
    /// <param name="mode">La modalità di allineamento.</param>
    /// <param name="currentCoordinates">L'elenco delle coordinate correnti.</param>
    /// <param name="totalCount">Il numero totale di immagini.</param>
    /// <returns>True se il calcolo è abilitato, altrimenti False.</returns>
    bool CanCalculate(
        AlignmentMode mode, 
        IEnumerable<Point?> currentCoordinates, 
        int totalCount);
}