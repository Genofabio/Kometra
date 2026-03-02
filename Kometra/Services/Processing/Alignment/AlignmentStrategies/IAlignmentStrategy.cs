using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Kometra.Models.Fits;
using Kometra.Models.Primitives;
using Kometra.Models.Processing;
using Kometra.Models.Processing.Alignment;

namespace Kometra.Services.Processing.Alignment.AlignmentStrategies;

public interface IAlignmentStrategy
{
    /// <summary>
    /// Esegue l'analisi della sequenza. 
    /// Riceve i file (identità) e i guesses (stato sessione) separatamente.
    /// </summary>
    Task<Point2D?[]> CalculateAsync(
        IEnumerable<FitsFileReference> files,
        IEnumerable<Point2D?> guesses, // <--- Parametro fondamentale per il purismo
        int searchRadius,
        IProgress<AlignmentProgressReport>? progress = null,
        CancellationToken token = default);
}