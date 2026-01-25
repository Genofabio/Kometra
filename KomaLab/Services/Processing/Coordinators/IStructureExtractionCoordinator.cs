using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KomaLab.Models.Fits;
using KomaLab.Models.Processing;
using KomaLab.Models.Processing.Enhancement;
using KomaLab.Services.Processing.Batch;

namespace KomaLab.Services.Processing.Coordinators;

public interface IStructureExtractionCoordinator
{
    /// <summary>
    /// Esegue il calcolo completo sui dati raw e restituisce l'array di pixel processati.
    /// Il tipo di ritorno è Array (può essere float[] o double[]) per rispettare la precisione originale.
    /// </summary>
    Task<Array> CalculatePreviewDataAsync(
        FitsFileReference sourceFile,
        StructureExtractionMode mode,
        StructureExtractionParameters parameters);

    /// <summary>
    /// Esegue l'elaborazione su file multipli salvando i risultati su disco.
    /// </summary>
    Task<List<string>> ExecuteBatchAsync(
        IEnumerable<FitsFileReference> sourceFiles,
        StructureExtractionMode mode,
        StructureExtractionParameters parameters,
        IProgress<BatchProgressReport>? progress = null,
        CancellationToken token = default);
}