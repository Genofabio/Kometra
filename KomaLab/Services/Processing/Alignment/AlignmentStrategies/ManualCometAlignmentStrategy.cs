using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KomaLab.Models.Fits;
using KomaLab.Models.Primitives;
using KomaLab.Models.Processing;
using KomaLab.Services.Fits;
using KomaLab.Services.Fits.Conversion;
using KomaLab.Services.Fits.Metadata;
using KomaLab.Services.Processing.Engines;
using OpenCvSharp;

namespace KomaLab.Services.Processing.Alignment.AlignmentStrategies;

public class ManualCometAlignmentStrategy : AlignmentStrategyBase
{
    private readonly IFitsMetadataService _metadataService;
    private readonly IFitsOpenCvConverter _converter;
    private readonly IImageAnalysisEngine _analysis;
    private readonly CenteringMethod _method;

    public ManualCometAlignmentStrategy(
        IFitsDataManager dataManager,
        IFitsMetadataService metadataService,
        IFitsOpenCvConverter converter, 
        IImageAnalysisEngine analysis,
        CenteringMethod method) : base(dataManager, analysis)
    {
        _metadataService = metadataService;
        _converter = converter;
        _analysis = analysis;
        _method = method;
    }

    public override async Task<Point2D?[]> CalculateAsync(
        IEnumerable<FitsFileReference> files, 
        IEnumerable<Point2D?> guesses, 
        int searchRadius, 
        IProgress<AlignmentProgressReport>? progress = null,
        CancellationToken token = default)
    {
        var fileList = files.ToList();
        var guessList = guesses.ToList();
        var results = new Point2D?[fileList.Count];

        int maxConcurrency = GetOptimalConcurrency(fileList[0]);
        using var semaphore = new SemaphoreSlim(maxConcurrency);
        var tasks = new List<Task>();

        for (int i = 0; i < fileList.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            int index = i;
            var fileRef = fileList[index]; // Prendiamo il Reference completo
            var guess = (index < guessList.Count) ? guessList[index] : null;

            if (guess == null) {
                results[index] = null;
                continue; 
            }

            tasks.Add(Task.Run(async () =>
            {
                await semaphore.WaitAsync(token);
                try
                {
                    if (searchRadius <= 0) {
                        results[index] = guess;
                    }
                    else {
                        // Raffinamento sub-pixel: passiamo il fileRef per i metadati di sessione
                        results[index] = await ExecuteWithRetryAsync(
                            operation: async () => await RefineCenterCoreAsync(fileRef, guess.Value, searchRadius),
                            itemIndex: index
                        ) ?? guess;
                    }

                    progress?.Report(new AlignmentProgressReport { 
                        CurrentIndex = index + 1, 
                        TotalCount = fileList.Count, 
                        FileName = System.IO.Path.GetFileName(fileRef.FilePath),
                        FoundCenter = results[index], 
                        Message = "Raffinamento manuale completato." 
                    });
                }
                finally { semaphore.Release(); }
            }, token));
        }

        await Task.WhenAll(tasks);
        return results;
    }

    private async Task<Point2D?> RefineCenterCoreAsync(FitsFileReference fileRef, Point2D guess, int radius)
    {
        // 1. Recupero dati originali dalla cache
        var data = await DataManager.GetDataAsync(fileRef.FilePath);
        
        // 2. PRIORITÀ HEADER: Rispettiamo BSCALE/BZERO modificati in RAM
        var header = fileRef.ModifiedHeader ?? data.Header;
        double bScale = _metadataService.GetDoubleValue(header, "BSCALE", 1.0);
        double bZero = _metadataService.GetDoubleValue(header, "BZERO", 0.0);

        // 3. SMART PROMOTION: Passando null, il convertitore sceglie 32 o 64 bit 
        // in base al BITPIX originale del file, garantendo integrità scientifica.
        using var fullMat = _converter.RawToMat(data.PixelData, bScale, bZero);
        
        // ====================================================================
        // 4. SANITIZZAZIONE E CROP DINAMICO
        // ====================================================================
        // Ritagliamo l'immagine sui dati validi, espandendo se necessario per includere il guess.
        // I NaN interni vengono sostituiti con 0.0 per permettere l'analisi matematica.
        // 'workMat' è l'immagine pulita, 'offset' è la posizione del crop rispetto all'originale.
        var (workMat, offset) = SanitizeAndCrop(fullMat, guess);

        using (workMat)
        {
            // Adattiamo il guess (Globale) in coordinate locali del crop
            Point2D localGuess = new Point2D(guess.X - offset.X, guess.Y - offset.Y);

            // Controllo limiti (se per caso il guess è ancora fuori dopo l'espansione, scenario estremo)
            if (localGuess.X < 0 || localGuess.Y < 0 || 
                localGuess.X >= workMat.Width || localGuess.Y >= workMat.Height)
                return guess;

            // ====================================================================
            // 5. ESECUZIONE ROI (Regione di interesse sui dati puliti)
            // ====================================================================
            var roi = new Rect(
                (int)(localGuess.X - radius), 
                (int)(localGuess.Y - radius), 
                radius * 2, radius * 2
            ).Intersect(new Rect(0, 0, workMat.Width, workMat.Height));

            if (roi.Width <= 4 || roi.Height <= 4) return guess;

            using var roiMat = new Mat(workMat, roi);
            
            // 6. Analisi del centroide con il metodo scelto dall'utente
            Point2D localCenter = _method switch {
                CenteringMethod.Centroid => _analysis.FindCentroid(roiMat),
                CenteringMethod.GaussianFit => _analysis.FindGaussianCenter(roiMat),
                CenteringMethod.Peak => _analysis.FindPeak(roiMat),
                _ => _analysis.FindCenterOfLocalRegion(roiMat)
            };
            
            // ====================================================================
            // 7. RICONVERSIONE COORDINATE GLOBALI
            // ====================================================================
            // Sommiamo l'offset del crop, l'offset della ROI e il risultato locale
            return new Point2D(
                localCenter.X + roi.X + offset.X, 
                localCenter.Y + roi.Y + offset.Y
            );
        }
    }
}