using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KomaLab.Models.Fits;
using KomaLab.Models.Fits.Structure;
using KomaLab.Models.Processing;
using OpenCvSharp;

namespace KomaLab.Services.Processing.Batch;

/// <summary>
/// Esecutore universale per operazioni batch N-immagini -> N-immagini.
/// Gestisce il ciclo di vita del file: Caricamento, Trasformazione, Salvataggio e Progresso.
/// </summary>
public interface IBatchProcessingService
{
    // Vecchio metodo (mantiene la compatibilità col resto del progetto)
    Task<List<string>> ProcessFilesAsync(
        IEnumerable<FitsFileReference> sourceFiles,
        string outputFolder,
        Action<Mat, Mat, FitsHeader, int> processor,
        IProgress<BatchProgressReport>? progress = null,
        CancellationToken token = default);

    // NUOVO overload (per gestire il tuo StructureExtractionCoordinator)
    Task<List<string>> ProcessFilesAsync(
        IEnumerable<FitsFileReference> sourceFiles,
        string outputFolder,
        Func<Mat, Mat, FitsHeader, int, Task> processor,
        IProgress<BatchProgressReport>? progress = null,
        CancellationToken token = default);
}