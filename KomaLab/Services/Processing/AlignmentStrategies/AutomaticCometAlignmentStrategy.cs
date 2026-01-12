using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using KomaLab.Models.Primitives;
using KomaLab.Services.Fits;

namespace KomaLab.Services.Processing.AlignmentStrategies;

// ---------------------------------------------------------------------------
// FILE: AutomaticCometAlignmentStrategy.cs
// RUOLO: Strategia Allineamento (Blind / Global)
// DESCRIZIONE:
// Implementa la ricerca "alla cieca" del punto più luminoso dell'immagine.
// È utile per comete o stelle singole molto luminose dove non si dispone
// di coordinate WCS o input utente.
//
// CAMBIAMENTI:
// - Aggiornato per usare IFitsIoService.
// - Rimossa logica di retry manuale (già gestita dal FileStreamProvider).
// ---------------------------------------------------------------------------

public class AutomaticCometAlignmentStrategy : IAlignmentStrategy
{
    private readonly IFitsIoService _ioService;      // Sostituisce il vecchio FitsService
    private readonly IFitsImageDataConverter _converter;
    private readonly IImageAnalysisService _analysis;

    public AutomaticCometAlignmentStrategy(
        IFitsIoService ioService, 
        IFitsImageDataConverter converter, 
        IImageAnalysisService analysis)
    {
        _ioService = ioService ?? throw new ArgumentNullException(nameof(ioService));
        _converter = converter ?? throw new ArgumentNullException(nameof(converter));
        _analysis = analysis ?? throw new ArgumentNullException(nameof(analysis));
    }

    public async Task<Point2D?[]> CalculateAsync(
        List<string> sourcePaths, 
        List<Point2D?> guesses, 
        int searchRadius, 
        IProgress<(int Index, Point2D? Center)>? progress)
    {
        int n = sourcePaths.Count;
        var results = new Point2D?[n];

        // Configurazione Concorrenza Adattiva
        // Se i file sono enormi (>100MB), elaboriamo uno alla volta per non saturare la RAM.
        long firstFileSize = 0;
        try { if (n > 0) firstFileSize = new FileInfo(sourcePaths[0]).Length; } catch { }
        
        int maxConcurrency = (firstFileSize > 100 * 1024 * 1024) 
            ? 1 
            : Math.Clamp(Environment.ProcessorCount / 2, 2, 4);

        using var semaphore = new SemaphoreSlim(maxConcurrency);
        var tasks = new List<Task>();

        for (int i = 0; i < n; i++)
        {
            int index = i;
            string path = sourcePaths[index];

            tasks.Add(Task.Run(async () =>
            {
                await semaphore.WaitAsync();
                try
                {
                    // Logica BLIND: Carica intera immagine -> Trova Max -> Ritorna
                    Point2D? result = await FindBrightestObjectAsync(path);
                    
                    results[index] = result;
                    progress?.Report((index, result));
                }
                finally { semaphore.Release(); }
            }));
        }

        await Task.WhenAll(tasks);
        return results;
    }

    private async Task<Point2D?> FindBrightestObjectAsync(string path)
    {
        try
        {
            // 1. Caricamento (la resilienza sui file lock è gestita internamente da IoService)
            var fitsData = await _ioService.LoadAsync(path);
            if (fitsData == null) return null;

            // 2. Conversione Raw -> OpenCV Mat
            using var mat = _converter.RawToMat(fitsData);
            
            // 3. Analisi Globale
            // Passiamo l'intera matrice all'analisi per trovare il centroide del blob più luminoso
            return _analysis.FindCenterOfLocalRegion(mat);
        }
        catch (Exception ex)
        {
            // Loggare l'errore in un sistema reale
            System.Diagnostics.Debug.WriteLine($"[AutoStrategy] Errore su {path}: {ex.Message}");
            return null;
        }
    }
}