using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KomaLab.Models.Fits;
using KomaLab.Models.Processing;
using KomaLab.Models.Processing.Enhancement;

namespace KomaLab.Services.Processing.Coordinators;

public interface IImageEnhancementCoordinator
{
    /// <summary>
    /// Calcola l'anteprima in RAM per una singola immagine.
    /// Restituisce l'array di pixel raw (di solito double/float) pronto per il renderer.
    /// </summary>
    Task<Array> CalculatePreviewDataAsync(
        FitsFileReference sourceFile,
        ImageEnhancementMode mode,
        ImageEnhancementParameters parameters);

    /// <summary>
    /// Esegue l'elaborazione batch su una lista di file, salvando i risultati su disco.
    /// Restituisce la lista dei percorsi dei file generati.
    /// </summary>
    Task<List<string>> ExecuteBatchAsync(
        IEnumerable<FitsFileReference> sourceFiles,
        ImageEnhancementMode mode,
        ImageEnhancementParameters parameters,
        IProgress<BatchProgressReport>? progress = null,
        CancellationToken token = default);
}