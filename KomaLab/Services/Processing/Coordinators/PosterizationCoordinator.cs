using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KomaLab.Models.Fits;
using KomaLab.Models.Fits.Structure;
using KomaLab.Models.Processing;
using KomaLab.Models.Visualization;
using KomaLab.Services.Fits.Metadata;
using KomaLab.Services.Processing.Batch;
using KomaLab.Services.Processing.Engines;
using KomaLab.Services.Processing.Rendering; // Necessario per IImagePresentationService
using OpenCvSharp;

namespace KomaLab.Services.Processing.Coordinators;

public class PosterizationCoordinator : IPosterizationCoordinator
{
    private readonly IBatchProcessingService _batchService;
    private readonly IImageEffectsEngine _effectsEngine;
    private readonly IFitsMetadataService _metadataService;
    private readonly IImagePresentationService _presentationService;

    public PosterizationCoordinator(
        IBatchProcessingService batchService,
        IImageEffectsEngine effectsEngine,
        IFitsMetadataService metadataService,
        IImagePresentationService presentationService)
    {
        _batchService = batchService;
        _effectsEngine = effectsEngine;
        _metadataService = metadataService;
        _presentationService = presentationService;
    }

    /// <summary>
    /// Genera l'azione di post-processing per il Renderer (Anteprima real-time).
    /// </summary>
    public Action<Mat> GetPreviewEffect(int levels)
    {
        return (mat) => 
        {
            // L'anteprima lavora su una Mat 8-bit (0-255) già normalizzata.
            // Applichiamo la posterizzazione lineare sul range del monitor.
            _effectsEngine.ApplyPosterization(mat, mat, levels, VisualizationMode.Linear, 0, 255);
        };
    }

    /// <summary>
    /// Esegue la posterizzazione su una lista di file, con supporto all'adattamento dinamico delle soglie.
    /// </summary>
    public async Task<List<string>> ExecuteBatchAsync(
        IEnumerable<FitsFileReference> sourceFiles,
        int levels,
        VisualizationMode mode,
        double blackPoint,
        double whitePoint,
        bool autoAdapt,                      // Nuovo parametro
        SigmaContrastProfile? sigmaProfile,  // Nuovo parametro (il profilo statistico)
        IProgress<BatchProgressReport>? progress = null,
        CancellationToken token = default)
    {
        Action<Mat, Mat, FitsHeader, int> posterizeOp = (src, dst, header, index) =>
        {
            double actualBlack = blackPoint;
            double actualWhite = whitePoint;

            // --- LOGICA DI ADATTAMENTO DINAMICO ---
            // Se l'adattamento è attivo, ricalcoliamo le soglie ADU per l'immagine corrente
            if (autoAdapt && sigmaProfile != null)
            {
                // 1. Analisi statistica dell'immagine corrente (Mean/StdDev)
                var stats = _presentationService.GetPresentationRequirements(src);
                
                // 2. Trasformazione del profilo Sigma in soglie ADU assolute per questo file
                var absolute = _presentationService.GetAbsoluteProfile(sigmaProfile, stats);
                actualBlack = absolute.BlackAdu;
                actualWhite = absolute.WhiteAdu;
            }

            // 3. Esecuzione dell'effetto (su dati scientifici Float/Double)
            _effectsEngine.ApplyPosterization(src, dst, levels, mode, actualBlack, actualWhite);

            // 4. DOCUMENTAZIONE (Solo HISTORY per preservare l'integrità dell'header)
            _metadataService.AddValue(header, "HISTORY", 
                $"KomaLab: Posterization ({levels} levels, {mode} scaling).");
            
            _metadataService.AddValue(header, "HISTORY", 
                $"KomaLab: Applied thresholds - Black: {actualBlack:F1}, White: {actualWhite:F1} (Adaptive: {autoAdapt})");

            // Aggiorniamo DATAMIN/MAX per aiutare la visualizzazione in altri software
            _metadataService.SetValue(header, "DATAMIN", 0.0);
            _metadataService.SetValue(header, "DATAMAX", (double)levels - 1);
        };

        // Delega al servizio batch che gestisce il loop, la memoria e il salvataggio
        return await _batchService.ProcessFilesAsync(
            sourceFiles, 
            "Posterized", 
            posterizeOp, 
            progress, 
            token);
    }
}