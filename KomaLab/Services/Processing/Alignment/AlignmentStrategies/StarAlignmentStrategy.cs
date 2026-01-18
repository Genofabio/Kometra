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

public class StarAlignmentStrategy : AlignmentStrategyBase
{
    private readonly IFitsMetadataService _metadataService;
    private readonly IFitsOpenCvConverter _converter;
    private readonly IImageAnalysisEngine _analysis;

    public StarAlignmentStrategy(
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
        IEnumerable<Point2D?> guesses, // Coordinate astrometriche opzionali
        int searchRadius, 
        IProgress<AlignmentProgressReport>? progress = null,
        CancellationToken token = default)
    {
        var fileList = files.ToList();
        var guessList = guesses?.ToList();
        var results = new Point2D?[fileList.Count];

        // Se abbiamo coordinate WCS (es. da Plate Solving), usiamo la modalità Aided
        bool hasWcs = guessList != null && guessList.Count > 0 && guessList[0].HasValue;

        if (hasWcs)
            await RunWcsAidedModeAsync(fileList, guessList!, results, progress, token);
        else
            await RunBlindFftModeAsync(fileList, results, progress, token);

        return results;
    }

    private async Task RunWcsAidedModeAsync(List<FitsFileReference> files, List<Point2D?> guesses, Point2D?[] results, IProgress<AlignmentProgressReport>? progress, CancellationToken token)
    {
        for (int i = 0; i < files.Count; i++) {
            token.ThrowIfCancellationRequested();
            results[i] = (i < guesses.Count && guesses[i].HasValue) ? guesses[i] : results[Math.Max(0, i - 1)];
            
            progress?.Report(new AlignmentProgressReport { 
                CurrentIndex = i + 1, TotalCount = files.Count, 
                FileName = System.IO.Path.GetFileName(files[i].FilePath),
                Message = "Allineamento tramite WCS..." 
            });
        }
    }

    private async Task RunBlindFftModeAsync(List<FitsFileReference> files, Point2D?[] results, IProgress<AlignmentProgressReport>? progress, CancellationToken token)
    {
        var firstHeader = await DataManager.GetHeaderOnlyAsync(files[0].FilePath);
        results[0] = new Point2D(_metadataService.GetIntValue(firstHeader, "NAXIS1") / 2.0, _metadataService.GetIntValue(firstHeader, "NAXIS2") / 2.0);

        Mat? prevMat = await LoadMatAsync(files[0].FilePath);
        if (prevMat == null) return;

        try {
            for (int i = 1; i < files.Count; i++) {
                token.ThrowIfCancellationRequested();
                using var currMat = await LoadMatAsync(files[i].FilePath);
                if (currMat != null) {
                    var shiftInfo = _analysis.ComputeStarFieldShift(prevMat, currMat);
                    results[i] = new Point2D(results[i - 1]!.Value.X + shiftInfo.Shift.X, results[i - 1]!.Value.Y + shiftInfo.Shift.Y);
                    
                    prevMat.Dispose();
                    prevMat = currMat.Clone();
                }
                progress?.Report(new AlignmentProgressReport { 
                    CurrentIndex = i + 1, TotalCount = files.Count, Message = "Analisi FFT..." 
                });
            }
        } finally { prevMat?.Dispose(); }
    }

    private async Task<Mat?> LoadMatAsync(string path) {
        var d = await DataManager.GetDataAsync(path);
        return _converter.RawToMat(d.PixelData, _metadataService.GetDoubleValue(d.Header, "BSCALE", 1.0));
    }
}