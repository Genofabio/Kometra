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

public class AutomaticCometAlignmentStrategy : AlignmentStrategyBase
{
    private readonly IFitsMetadataService _metadataService;
    private readonly IFitsOpenCvConverter _converter;
    private readonly IImageAnalysisEngine _analysis;

    public AutomaticCometAlignmentStrategy(
        IFitsDataManager dataManager,
        IFitsMetadataService metadataService,
        IFitsOpenCvConverter converter, 
        IImageAnalysisEngine analysis) : base(dataManager)
    {
        _metadataService = metadataService;
        _converter = converter;
        _analysis = analysis;
    }

    public override async Task<Point2D?[]> CalculateAsync(
        IEnumerable<FitsFileReference> files, 
        IEnumerable<Point2D?> guesses, // Dati passati al volo
        int searchRadius, 
        IProgress<AlignmentProgressReport>? progress = null,
        CancellationToken token = default)
    {
        var fileList = files.ToList();
        var guessList = guesses?.ToList(); // Usiamo i guesses passati
        var results = new Point2D?[fileList.Count];

        using var semaphore = new SemaphoreSlim(GetOptimalConcurrency(fileList[0]));
        var tasks = fileList.Select(async (file, i) =>
        {
            await semaphore.WaitAsync(token);
            try {
                token.ThrowIfCancellationRequested();
                
                // Se l'utente ha cliccato qualcosa, lo usiamo come suggerimento
                var guess = (guessList != null && i < guessList.Count) ? guessList[i] : null;

                results[i] = await ExecuteWithRetryAsync(
                    operation: async () => await FindObjectCoreAsync(file.FilePath, guess),
                    itemIndex: i
                ) ?? guess;

                progress?.Report(new AlignmentProgressReport {
                    CurrentIndex = i + 1, 
                    TotalCount = fileList.Count,
                    FileName = System.IO.Path.GetFileName(file.FilePath),
                    FoundCenter = results[i], 
                    Message = "Rilevamento automatico..."
                });
            }
            finally { semaphore.Release(); }
        });

        await Task.WhenAll(tasks);
        return results;
    }

    private async Task<Point2D?> FindObjectCoreAsync(string path, Point2D? guess)
    {
        var data = await DataManager.GetDataAsync(path);
        using var mat = _converter.RawToMat(data.PixelData, _metadataService.GetDoubleValue(data.Header, "BSCALE", 1.0));

        if (guess.HasValue) {
            int smartRadius = EstimateSmartRadius(mat);
            var roiRect = new Rect((int)(guess.Value.X - smartRadius), (int)(guess.Value.Y - smartRadius), smartRadius * 2, smartRadius * 2)
                .Intersect(new Rect(0, 0, mat.Width, mat.Height));

            if (roiRect.Width > 4 && roiRect.Height > 4) {
                using var crop = new Mat(mat, roiRect);
                var local = _analysis.FindCenterOfLocalRegion(crop);
                return new Point2D(local.X + roiRect.X, local.Y + roiRect.Y);
            }
        }

        return _analysis.FindCenterOfLocalRegion(mat);
    }

    private int EstimateSmartRadius(Mat mat) {
        int baseRadius = Math.Min(mat.Width, mat.Height) / 16;
        Cv2.MeanStdDev(mat, out Scalar mean, out Scalar stddev);
        using var thresh = new Mat();
        Cv2.Threshold(mat, thresh, mean.Val0 + (5 * stddev.Val0), 255, ThresholdTypes.Binary);
        double density = (double)Cv2.CountNonZero(thresh) / (mat.Width * mat.Height);
        return Math.Max(30, (int)(baseRadius * (density > 0.001 ? 0.4 : 1.0)));
    }
}