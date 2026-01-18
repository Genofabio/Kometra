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
        CenteringMethod method) : base(dataManager)
    {
        _metadataService = metadataService;
        _converter = converter;
        _analysis = analysis;
        _method = method;
    }

    public override async Task<Point2D?[]> CalculateAsync(
        IEnumerable<FitsFileReference> files, 
        IEnumerable<Point2D?> guesses, // Iniezione dei dati nel metodo
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
                    // Se raggio <= 0, ci fidiamo ciecamente del click utente
                    if (searchRadius <= 0) {
                        results[index] = guess;
                    }
                    else {
                        // Raffinamento sub-pixel con gestione RAM (ClearCache) automatica nella base
                        results[index] = await ExecuteWithRetryAsync(
                            operation: async () => await RefineCenterCoreAsync(fileList[index].FilePath, guess.Value, searchRadius),
                            itemIndex: index
                        ) ?? guess;
                    }

                    progress?.Report(new AlignmentProgressReport { 
                        CurrentIndex = index + 1, 
                        TotalCount = fileList.Count, 
                        FileName = System.IO.Path.GetFileName(fileList[index].FilePath),
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

    private async Task<Point2D?> RefineCenterCoreAsync(string path, Point2D guess, int radius)
    {
        var data = await DataManager.GetDataAsync(path);
        using var fullMat = _converter.RawToMat(data.PixelData, _metadataService.GetDoubleValue(data.Header, "BSCALE", 1.0));
        
        var roi = new Rect((int)(guess.X - radius), (int)(guess.Y - radius), radius * 2, radius * 2)
            .Intersect(new Rect(0, 0, fullMat.Width, fullMat.Height));

        if (roi.Width <= 4 || roi.Height <= 4) return guess;

        using var roiMat = new Mat(fullMat, roi);
        Point2D localCenter = _method switch {
            CenteringMethod.Centroid => _analysis.FindCentroid(roiMat),
            CenteringMethod.GaussianFit => _analysis.FindGaussianCenter(roiMat),
            CenteringMethod.Peak => _analysis.FindPeak(roiMat),
            _ => _analysis.FindCenterOfLocalRegion(roiMat)
        };
        
        return new Point2D(localCenter.X + roi.X, localCenter.Y + roi.Y);
    }
}