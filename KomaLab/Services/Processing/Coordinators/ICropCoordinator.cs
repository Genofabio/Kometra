using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KomaLab.Models.Fits;
using KomaLab.Models.Primitives;
using KomaLab.Models.Processing;

namespace KomaLab.Services.Processing.Coordinators;

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