using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KomaLab.Models.Fits;
using KomaLab.Models.Fits.Structure; // Aggiunto per FitsFileReference
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
        // L'anteprima lavora in-place sulla Mat del Renderer
        return (mat) => 
        {
            _effectsEngine.ApplyPosterization(mat, mat, levels, VisualizationMode.Linear, 0, 255);
        };
    }

    public async Task<List<string>> ExecuteBatchAsync(
        IEnumerable<FitsFileReference> sourceFiles,
        int levels,
        VisualizationMode mode,
        double blackPoint,
        double whitePoint,
        IProgress<BatchProgressReport>? progress = null,
        CancellationToken token = default)
    {
        // Aggiungiamo 'header' alla firma della lambda per farla combaciare con il BatchService
        Action<Mat, Mat, FitsHeader, int> posterizeOp = (src, dst, header, index) =>
        {
            // 1. Esecuzione dell'effetto sui pixel
            _effectsEngine.ApplyPosterization(src, dst, levels, mode, blackPoint, whitePoint);

            // 2. (Opzionale ma consigliato) Aggiornamento metadati
            // Anche se la posterizzazione non sposta l'immagine (quindi niente ShiftWcs),
            // è buona norma scrivere nell'header cosa è successo.
            header.AddCard(new FitsCard("HISTORY", $"KomaLab: Posterization applied ({levels} levels)"));
        };

        // Ora la chiamata non darà più errore di firma
        return await _batchService.ProcessFilesAsync(
            sourceFiles, 
            "Posterized", 
            posterizeOp, 
            progress, 
            token);
    }
}