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
    // --- Hook per l'Anteprima (UI) ---
    Action<Mat> GetPreviewEffect(int levels);

    // --- Elaborazione Massiva (Batch) ---
    Task<List<string>> ExecuteBatchAsync(
        IEnumerable<FitsFileReference> sourceFiles, // MODIFICATO: Da string a FitsFileReference
        int levels,
        VisualizationMode mode,
        double blackPoint,
        double whitePoint,
        IProgress<BatchProgressReport>? progress = null,
        CancellationToken token = default);
}