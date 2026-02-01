using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KomaLab.Models.Fits;
using KomaLab.Models.Processing;
using KomaLab.Models.Processing.Masking;

namespace KomaLab.Services.Processing.Coordinators;

public interface IMaskingCoordinator
{
    // Per l'anteprima (ritorna le maschere raw per visualizzazione)
    Task<(Array StarMaskPixels, Array CometMaskPixels)> CalculatePreviewAsync(
        FitsFileReference sourceFile, 
        MaskingParameters p, 
        CancellationToken token = default);

    // Per il batch (salva le maschere su disco)
    Task<List<string>> ExecuteBatchAsync(
        IEnumerable<FitsFileReference> sourceFiles,
        MaskingParameters p,
        IProgress<BatchProgressReport>? progress = null,
        CancellationToken token = default);
}