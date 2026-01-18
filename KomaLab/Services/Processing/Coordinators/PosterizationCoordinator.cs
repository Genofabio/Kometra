using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KomaLab.Models.Processing;
using KomaLab.Models.Visualization;
using KomaLab.Services.Processing.Batch;
using KomaLab.Services.Processing.Engines;
using OpenCvSharp;

namespace KomaLab.Services.Processing.Coordinators;

public class PosterizationCoordinator : IPosterizationCoordinator
{
    private readonly IBatchProcessingService _batchService;
    private readonly IImageEffectsEngine _effectsEngine;

    public PosterizationCoordinator(
        IBatchProcessingService batchService,
        IImageEffectsEngine effectsEngine)
    {
        _batchService = batchService;
        _effectsEngine = effectsEngine;
    }

    public Action<Mat> GetPreviewEffect(int levels)
    {
        return (mat) => 
        {
            _effectsEngine.ApplyPosterization(mat, mat, levels, VisualizationMode.Linear, 0, 255);
        };
    }

    public async Task<List<string>> ExecuteBatchAsync(
        IEnumerable<string> sourcePaths,
        int levels,
        VisualizationMode mode,
        double blackPoint,
        double whitePoint,
        IProgress<BatchProgressReport>? progress = null,
        CancellationToken token = default)
    {
        // Aggiungiamo l'indice '_' perché è richiesto dalla firma, ma lo ignoriamo
        Action<Mat, Mat, int> posterizeOp = (src, dst, _) =>
        {
            _effectsEngine.ApplyPosterization(src, dst, levels, mode, blackPoint, whitePoint);
        };

        return await _batchService.ProcessFilesAsync(sourcePaths, "Posterized", posterizeOp, progress, token);
    }
}