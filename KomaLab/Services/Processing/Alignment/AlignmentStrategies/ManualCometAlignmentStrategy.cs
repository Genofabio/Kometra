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

    public ManualCometAlignmentStrategy(
        IFitsDataManager dataManager,
        IFitsMetadataService metadataService,
        IFitsOpenCvConverter converter, 
        IImageAnalysisEngine analysis) : base(dataManager, analysis)
    {
        _metadataService = metadataService;
        _converter = converter;
        _analysis = analysis;
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
            var fileRef = fileList[index];
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
                        // Raffinamento sub-pixel con il metodo asimmetrico
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
        var data = await DataManager.GetDataAsync(fileRef.FilePath);
        var imageHdu = data.FirstImageHdu ?? data.PrimaryHdu;
        if (imageHdu == null) return guess; 

        var header = fileRef.ModifiedHeader ?? imageHdu.Header;
        double bScale = _metadataService.GetDoubleValue(header, "BSCALE", 1.0);
        double bZero = _metadataService.GetDoubleValue(header, "BZERO", 0.0);

        using var fullMat = _converter.RawToMat(imageHdu.PixelData, bScale, bZero);
        var (workMat, offset) = SanitizeAndCrop(fullMat, guess);

        using (workMat)
        {
            Point2D localGuess = new Point2D(guess.X - offset.X, guess.Y - offset.Y);

            if (localGuess.X < 0 || localGuess.Y < 0 || 
                localGuess.X >= workMat.Width || localGuess.Y >= workMat.Height)
                return guess;

            var roi = new Rect(
                (int)(localGuess.X - radius), 
                (int)(localGuess.Y - radius), 
                radius * 2, radius * 2
            ).Intersect(new Rect(0, 0, workMat.Width, workMat.Height));

            if (roi.Width <= 4 || roi.Height <= 4) return guess;

            using var roiMat = new Mat(workMat, roi);
            
            // 6. FORZATURA DEL METODO ASIMMETRICO A QUADRANTI
            Point2D localCenter = _analysis.FindAsymmetricQuadrantCenter(roiMat);
            
            // 7. RICONVERSIONE COORDINATE GLOBALI
            Point2D globalCenter = new Point2D(
                localCenter.X + roi.X + offset.X, 
                localCenter.Y + roi.Y + offset.Y
            );

            // ====================================================================
            // LOG DI DEBUG GLOBALE PER TRACCIARE L'INSTABILITÀ
            // ====================================================================
            System.Diagnostics.Debug.WriteLine($"\n--- DEBUG ALLINEAMENTO MANUALE ---");
            System.Diagnostics.Debug.WriteLine($"File: {System.IO.Path.GetFileName(fileRef.FilePath)}");
            System.Diagnostics.Debug.WriteLine($"Guess Iniziale (Globale): X={guess.X:F2}, Y={guess.Y:F2}");
            System.Diagnostics.Debug.WriteLine($"Centro Trovato (Globale): X={globalCenter.X:F2}, Y={globalCenter.Y:F2}");
            System.Diagnostics.Debug.WriteLine($"Spostamento: Delta X={globalCenter.X - guess.X:F2}, Delta Y={globalCenter.Y - guess.Y:F2}");
            System.Diagnostics.Debug.WriteLine("----------------------------------\n");

            return globalCenter;
        }
    }
}