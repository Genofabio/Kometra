using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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
        IEnumerable<string> sourcePaths,
        string outputFolder,
        Action<Mat, Mat, int> processor, // Aggiunto 'int' per l'indice
        IProgress<BatchProgressReport>? progress = null,
        CancellationToken token = default);
}