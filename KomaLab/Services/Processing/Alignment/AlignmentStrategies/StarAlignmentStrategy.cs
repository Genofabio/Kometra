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
        var guessList = guesses?.ToList();
        var results = new Point2D?[fileList.Count];

        // Rilevamento WCS: Se il primo frame ha coordinate WCS, usiamo quelle come ancora
        bool hasWcsInput = (guessList != null && guessList.Count > 0 && guessList[0].HasValue);

        if (hasWcsInput)
        {
            await RunWcsPriorityModeAsync(fileList, guessList!, results, progress, token);
        }
        else
        {
            await RunVisualBlindModeAsync(fileList, results, progress, token);
        }

        return results;
    }

    // =======================================================================
    // MODALITÀ 1: WCS PRIORITARIO (Basato su coordinate astronomiche)
    // =======================================================================
    
    private async Task RunWcsPriorityModeAsync(
        List<FitsFileReference> files, 
        List<Point2D?> guesses, 
        Point2D?[] results, 
        IProgress<AlignmentProgressReport>? progress, 
        CancellationToken token)
    {
        // 1. Calcolo geometria di riferimento sul Frame 0
        var header0 = files[0].ModifiedHeader ?? await DataManager.GetHeaderOnlyAsync(files[0].FilePath);
        double w0 = _metadataService.GetIntValue(header0!, "NAXIS1");
        double h0 = _metadataService.GetIntValue(header0!, "NAXIS2");
        
        Point2D geoCenter0 = new Point2D(w0 / 2.0, h0 / 2.0);
        Point2D wcsPoint0 = guesses[0]!.Value; 

        // Delta necessario per centrare l'oggetto WCS nel canvas geometrico
        double deltaX = geoCenter0.X - wcsPoint0.X;
        double deltaY = geoCenter0.Y - wcsPoint0.Y;

        results[0] = geoCenter0;
        ReportProgress(progress, 0, files.Count, files[0].FilePath, results[0], "Ancoraggio WCS completato.");

        for (int i = 1; i < files.Count; i++)
        {
            token.ThrowIfCancellationRequested();

            // CASO A: Il frame corrente ha dati WCS validi
            if (i < guesses.Count && guesses[i].HasValue)
            {
                Point2D wcsPointI = guesses[i]!.Value;
                results[i] = new Point2D(wcsPointI.X + deltaX, wcsPointI.Y + deltaY);
                
                ReportProgress(progress, i, files.Count, files[i].FilePath, results[i], "Allineamento astronomico (WCS)...");
            }
            // CASO B: Fallback su FFT se mancano dati WCS nel mezzo della sequenza
            else
            {
                // Passiamo i Reference invece dei path per mantenere la coerenza scientifica
                Point2D shift = await CalculateShiftWithRetryAsync(files[i - 1], files[i]);
                
                Point2D prevCenter = results[i - 1] ?? geoCenter0;
                results[i] = new Point2D(prevCenter.X + shift.X, prevCenter.Y + shift.Y);
                
                ReportProgress(progress, i, files.Count, files[i].FilePath, results[i], "Allineamento visivo (FFT Fallback)...");
            }
        }
    }

    // =======================================================================
    // MODALITÀ 2: FFT PURA (Rilevamento automatico degli spostamenti)
    // =======================================================================

    private async Task RunVisualBlindModeAsync(
        List<FitsFileReference> files, 
        Point2D?[] results, 
        IProgress<AlignmentProgressReport>? progress, 
        CancellationToken token)
    {
        var firstHeader = files[0].ModifiedHeader ?? await DataManager.GetHeaderOnlyAsync(files[0].FilePath);
        double w = _metadataService.GetIntValue(firstHeader!, "NAXIS1");
        double h = _metadataService.GetIntValue(firstHeader!, "NAXIS2");
        
        results[0] = new Point2D(w / 2.0, h / 2.0);
        ReportProgress(progress, 0, files.Count, files[0].FilePath, results[0], "Riferimento geometrico fissato.");

        // Caricamento del primo frame con Smart Promotion (rispetta BITPIX originale)
        Mat? prevMat = await LoadMatWithRetryAsync(files[0]);
        if (prevMat == null) return; 

        try
        {
            for (int i = 1; i < files.Count; i++)
            {
                token.ThrowIfCancellationRequested();

                using Mat? currentMat = await LoadMatWithRetryAsync(files[i]);

                if (currentMat != null)
                {
                    // Calcolo dello shift nel dominio della frequenza (FFT)
                    var shiftResult = await Task.Run(() => _analysis.ComputeStarFieldShift(prevMat, currentMat));
                    Point2D shift = shiftResult.Shift;

                    Point2D prevCenter = results[i - 1]!.Value;
                    results[i] = new Point2D(prevCenter.X + shift.X, prevCenter.Y + shift.Y);

                    // Scambio matrici per il frame successivo: smaltiamo la vecchia per risparmiare RAM
                    prevMat.Dispose();       
                    prevMat = currentMat.Clone(); // Clone necessario perché currentMat è 'using'
                }
                else
                {
                    results[i] = results[i - 1];
                }

                ReportProgress(progress, i, files.Count, files[i].FilePath, results[i], "Analisi shift campo stellare...");
            }
        }
        finally
        {
            prevMat?.Dispose();
        }
    }

    // =======================================================================
    // HELPERS DI CARICAMENTO (Corretti per Nullable Structs)
    // =======================================================================

    private async Task<Mat?> LoadMatWithRetryAsync(FitsFileReference fileRef)
    {
        // Mat è una classe (Reference Type), quindi qui il ?? o il ritorno null sono già legali
        return await ExecuteWithRetryAsync<Mat?>(async () =>
        {
            var data = await DataManager.GetDataAsync(fileRef.FilePath);
            var header = fileRef.ModifiedHeader ?? data.Header;
            double bScale = _metadataService.GetDoubleValue(header, "BSCALE", 1.0);
            double bZero = _metadataService.GetDoubleValue(header, "BZERO", 0.0);

            var mat = _converter.RawToMat(data.PixelData, bScale, bZero);

            // GESTIONE NAN PER FFT:
            // Sostituiamo i NaN (bordi o pixel caldi) con 0.0 mantenendo la dimensione originale.
            // Il crop falserebbe la correlazione di fase perché sposterebbe il centro geometrico.
            PatchNaNsInPlace(mat);

            return mat;
        }, -1);
    }

    private async Task<Point2D> CalculateShiftWithRetryAsync(FitsFileReference prev, FitsFileReference curr)
    {
        // Forziamo il tipo generico a Point2D? (nullable struct)
        var result = await ExecuteWithRetryAsync<Point2D?>(async () =>
        {
            using var m1 = await LoadMatWithRetryAsync(prev);
            using var m2 = await LoadMatWithRetryAsync(curr);
        
            if (m1 == null || m2 == null) return null;

            var res = _analysis.ComputeStarFieldShift(m1, m2);
            return res.Shift;
        }, -1);

        // Se il retry restituisce null, torniamo uno shift zero
        return result ?? new Point2D(0, 0);
    }

    private void ReportProgress(IProgress<AlignmentProgressReport>? progress, int index, int total, string path, Point2D? center, string msg)
    {
        progress?.Report(new AlignmentProgressReport {
            CurrentIndex = index + 1,
            TotalCount = total,
            FileName = System.IO.Path.GetFileName(path),
            FoundCenter = center,
            Message = msg
        });
    }
}