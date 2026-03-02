using System.Collections.Generic;
using Kometra.Models.Primitives;

namespace Kometra.Models.Processing.Alignment;

/// <summary>
/// Rappresenta il risultato della fase di analisi dell'allineamento.
/// Contiene tutte le informazioni geometriche necessarie per la fase di warping.
/// </summary>
public class AlignmentMap
{
    // Il centro di riferimento (stella o cometa) per ogni file
    public List<Point2D?> Centers { get; init; } = new();

    // La dimensione finale del canvas (calcolata per contenere tutte le immagini)
    public Size2D TargetSize { get; init; }

    // La correzione globale dell'offset (necessaria per Star Alignment)
    public Point2D GlobalShift { get; init; }

    // Riferimento al target originale
    public AlignmentTarget Target { get; init; }

    public bool IsValid => Centers.Count > 0 && TargetSize.Width > 0;
}