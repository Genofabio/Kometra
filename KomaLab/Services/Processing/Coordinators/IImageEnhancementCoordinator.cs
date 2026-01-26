using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KomaLab.Models.Fits;
using KomaLab.Models.Processing;
using KomaLab.Models.Processing.Enhancement;

namespace KomaLab.Services.Processing.Coordinators;

/// <summary>
/// Coordina l'applicazione dei filtri di enhancement astronomico (Larson-Sekanina, RVSF, etc.)
/// orchestrando i motori di calcolo, la gestione dei metadati e il processamento batch.
/// </summary>
public interface IImageEnhancementCoordinator
{
    /// <summary>
    /// Calcola l'anteprima in RAM per una singola immagine.
    /// Restituisce l'array di pixel raw (di solito double/float) pronto per il renderer.
    /// Supporta la cancellazione per interrompere calcoli obsoleti durante lo spostamento degli slider.
    /// </summary>
    Task<Array> CalculatePreviewDataAsync(
        FitsFileReference sourceFile,
        ImageEnhancementMode mode,
        ImageEnhancementParameters parameters,
        CancellationToken token = default); // <--- AGGIUNTO PER SICUREZZA UI

    /// <summary>
    /// Esegue l'elaborazione batch su una lista di file, applicando i filtri e salvando i risultati su disco.
    /// Restituisce la lista dei percorsi dei file generati.
    /// </summary>
    Task<List<string>> ExecuteBatchAsync(
        IEnumerable<FitsFileReference> sourceFiles,
        ImageEnhancementMode mode,
        ImageEnhancementParameters parameters,
        IProgress<BatchProgressReport>? progress = null,
        CancellationToken token = default);
}