using KomaLab.ViewModels; // Per AlignmentMode
using System.Collections.Generic;
using System.Threading.Tasks;
using Point = Avalonia.Point;
using Size = Avalonia.Size;

namespace KomaLab.Services;

// (L'enum 'CenteringMethod' è stato rimosso da qui)

/// <summary>
/// Definisce il contratto per il servizio di logica di business dell'allineamento.
/// </summary>
public interface IAlignmentService
{
    /// <summary>
    /// Calcola i centri delle immagini in base alla modalità e alle coordinate fornite.
    /// </summary>
    Task<IEnumerable<Point?>> CalculateCentersAsync(
        AlignmentMode mode, 
        IEnumerable<Point?> currentCoordinates, 
        Size imageSize);

    /// <summary>
    /// Determina se il calcolo può essere eseguito in base allo stato corrente.
    /// </summary>
    bool CanCalculate(
        AlignmentMode mode, 
        IEnumerable<Point?> currentCoordinates, 
        int totalCount);
}