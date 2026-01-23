using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KomaLab.Models.Fits;
using KomaLab.Models.Processing;
using KomaLab.Models.Visualization;
using OpenCvSharp;

namespace KomaLab.Services.Processing.Coordinators;

public interface IPosterizationCoordinator
{
    /// <summary>
    /// Genera l'azione di post-processing per l'anteprima real-time.
    /// </summary>
    Action<Mat> GetPreviewEffect(int levels);

    /// <summary>
    /// Esegue la posterizzazione massiva. 
    /// Supporta l'adattamento dinamico delle soglie basato sulle statistiche di ogni singolo frame.
    /// </summary>
    Task<List<string>> ExecuteBatchAsync(
        IEnumerable<FitsFileReference> sourceFiles,
        int levels,
        VisualizationMode mode,
        double blackPoint,
        double whitePoint,
        bool autoAdapt,                      // AGGIUNTO: Per gestire l'opzione "Adatta luminosità"
        SigmaContrastProfile? sigmaProfile,  // AGGIUNTO: Il profilo Sigma da replicare nel batch
        IProgress<BatchProgressReport>? progress = null,
        CancellationToken token = default);
}