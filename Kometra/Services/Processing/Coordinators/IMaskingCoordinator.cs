using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Kometra.Services.Processing.Batch;
using Kometra.Models.Fits;
using Kometra.Models.Processing;
using Kometra.Models.Processing.Batch;
using Kometra.Models.Processing.Masking;

namespace Kometra.Services.Processing.Coordinators;

public interface IMaskingCoordinator
{
    // [FIX] Ora restituisce un singolo Array (l'immagine Starless), non più una Tuple
    Task<Array> ProcessPreviewAsync(
        FitsFileReference sourceFile, 
        MaskingParameters p, 
        CancellationToken token = default);

    Task<List<string>> ExecuteBatchAsync(
        IEnumerable<FitsFileReference> sourceFiles,
        MaskingParameters p,
        IProgress<BatchProgressReport>? progress = null,
        CancellationToken token = default);

    Task<(Array StarMask, Array CometMask)> CalculateMasksPreviewAsync(
        FitsFileReference sourceFile,
        MaskingParameters p,
        CancellationToken token = default);
}