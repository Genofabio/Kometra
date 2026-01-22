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
    /// <summary>
    /// Esegue una trasformazione su una lista di file FITS.
    /// </summary>
    /// <param name="sourcePaths">Percorsi dei file originali.</param>
    /// <param name="outputFolder">Cartella di destinazione.</param>
    /// <param name="processor">Azione da eseguire sulla Matrice (riceve la Mat sorgente e quella di destinazione).</param>
    /// <param name="progress">Callback per l'aggiornamento della UI.</param>
    /// <param name="token">Token per annullare l'operazione.</param>
    /// <returns>Lista dei percorsi dei file generati.</returns>
    Task<List<string>> ProcessFilesAsync(
        IEnumerable<FitsFileReference> sourceFiles,
        string outputFolder,
        // Aggiornata la firma dell'Action qui sotto
        Action<Mat, Mat, FitsHeader, int> processor, 
        IProgress<BatchProgressReport>? progress = null,
        CancellationToken token = default);
}