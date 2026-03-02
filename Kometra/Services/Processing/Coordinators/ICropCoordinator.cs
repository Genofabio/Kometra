using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Kometra.Models.Fits;
using Kometra.Models.Primitives;
using Kometra.Models.Processing;
using Kometra.Models.Processing.Batch;

namespace Kometra.Services.Processing.Coordinators;

public enum CropMode
{
    Static,  // Un solo punto applicato a tutte le immagini
    Dynamic  // Un punto diverso per ogni immagine
}

public interface ICropCoordinator
{
    /// <summary>
    /// Analizza la lista di file per trovare le dimensioni minime (Width/Height)
    /// che limiteranno la dimensione massima del rettangolo di crop.
    /// </summary>
    Task<Size2D> AnalyzeSequenceLimitsAsync(IEnumerable<FitsFileReference> files);

    /// <summary>
    /// Esegue il batch di ritaglio.
    /// </summary>
    Task<List<string>> ExecuteCropBatchAsync(
        IEnumerable<FitsFileReference> files,
        List<Point2D?> centers,
        Size2D cropSize,
        IProgress<BatchProgressReport>? progress = null,
        CancellationToken token = default);
}