using KomaLab.ViewModels; // Per AlignmentMode
using System.Collections.Generic;
using System.Threading.Tasks;
using KomaLab.Models;
using Point = Avalonia.Point;

namespace KomaLab.Services;

// (L'enum 'CenteringMethod' è stato rimosso da qui)

/// <summary>
/// Definisce il contratto per il servizio di logica di business dell'allineamento.
/// </summary>
public interface IAlignmentService
{
    // --- MODIFICATA ---
    /// <summary>
    /// Calcola i centri di allineamento (Modalità Manuale).
    /// </summary>
    Task<IEnumerable<Point?>> CalculateCentersAsync(
        AlignmentMode mode, 
        List<FitsImageData?> sourceData, // <-- Accetta i dati, non i path
        IEnumerable<Point?> currentCoordinates, 
        int searchRadius);

    /// <summary>
    /// Determina se il calcolo può essere eseguito in base allo stato corrente.
    /// </summary>
    bool CanCalculate(
        AlignmentMode mode, 
        IEnumerable<Point?> currentCoordinates, 
        int totalCount);
    
    /// <summary>
    /// Applica i centri calcolati ai dati sorgente e restituisce i nuovi dati processati.
    /// </summary>
    Task<List<FitsImageData?>> ApplyCenteringAsync(List<FitsImageData?> sourceData,
        List<Point?> centers);
}