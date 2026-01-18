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

public class GuidedCometAlignmentStrategy : AlignmentStrategyBase
{
    private readonly IFitsMetadataService _metadataService;
    private readonly IFitsOpenCvConverter _converter;
    private readonly IImageAnalysisEngine _analysis;
    private readonly IGeometricEngine _geometricEngine;

    public GuidedCometAlignmentStrategy(
        IFitsDataManager dataManager,
        IFitsMetadataService metadataService,
        IFitsOpenCvConverter converter, 
        IImageAnalysisEngine analysis,
        IGeometricEngine geometricEngine) : base(dataManager)
    {
        _metadataService = metadataService;
        _converter = converter;
        _analysis = analysis;
        _geometricEngine = geometricEngine;
    }

    public override async Task<Point2D?[]> CalculateAsync(
        IEnumerable<FitsFileReference> files, 
        IEnumerable<Point2D?> guesses, // Le ancore fornite dalla UI
        int searchRadius, 
        IProgress<AlignmentProgressReport>? progress = null,
        CancellationToken token = default)
    {
        var fileList = files.ToList();
        var anchorList = guesses.ToList();
        int n = fileList.Count;
        var results = new Point2D?[n];

        // Le ancore sono i punti certi forniti dall'utente (es. frame 1 e frame N)
        var p1 = anchorList.FirstOrDefault(p => p.HasValue);
        var pN = anchorList.LastOrDefault(p => p.HasValue);

        if (n < 2 || !p1.HasValue || !pN.HasValue) 
            return anchorList.ToArray();

        using var semaphore = new SemaphoreSlim(GetOptimalConcurrency(fileList[0]));
        Mat? templateMat = null;

        try
        {
            // 1. ESTRAZIONE TEMPLATE (Frame 0)
            var startData = await DataManager.GetDataAsync(fileList[0].FilePath);
            using var startMat = _converter.RawToMat(startData.PixelData, 
                _metadataService.GetDoubleValue(startData.Header, "BSCALE", 1.0));
            
            templateMat = _geometricEngine.ExtractRegion(startMat, p1.Value, searchRadius);
            results[0] = p1;

            // 2. CALCOLO TRAIETTORIA LINEARE
            double stepX = (pN.Value.X - p1.Value.X) / (n - 1);
            double stepY = (pN.Value.Y - p1.Value.Y) / (n - 1);

            // 3. TRACKING PARALLELO
            var tasks = Enumerable.Range(1, n - 1).Select(async i =>
            {
                await semaphore.WaitAsync(token);
                try {
                    token.ThrowIfCancellationRequested();
                    
                    var expected = new Point2D(p1.Value.X + (i * stepX), p1.Value.Y + (i * stepY));

                    results[i] = await ExecuteWithRetryAsync(
                        operation: async () => await ProcessFrameWithTemplateAsync(fileList[i].FilePath, templateMat, expected, searchRadius),
                        itemIndex: i
                    ) ?? expected;

                    progress?.Report(new AlignmentProgressReport { 
                        CurrentIndex = i + 1, 
                        TotalCount = n, 
                        FoundCenter = results[i], 
                        Message = $"Tracking cometa frame {i+1}..." 
                    });
                }
                finally { semaphore.Release(); }
            });

            await Task.WhenAll(tasks);
        }
        finally { templateMat?.Dispose(); }

        return results;
    }

    private async Task<Point2D?> ProcessFrameWithTemplateAsync(string path, Mat template, Point2D expected, int radius)
    {
        var data = await DataManager.GetDataAsync(path);
        using var fullImage = _converter.RawToMat(data.PixelData, _metadataService.GetDoubleValue(data.Header, "BSCALE", 1.0));

        return _analysis.FindTemplatePosition(fullImage, template, expected, radius);
    }
}