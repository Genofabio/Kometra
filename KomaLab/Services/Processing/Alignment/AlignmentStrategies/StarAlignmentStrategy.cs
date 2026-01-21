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
        IEnumerable<Point2D?> guesses, 
        int searchRadius, 
        IProgress<AlignmentProgressReport>? progress = null,
        CancellationToken token = default)
    {
        var fileList = files.ToList();
        var guessList = guesses?.ToList();
        var results = new Point2D?[fileList.Count];

        // Rilevamento: Se abbiamo un guess valido nel primo frame (WCS), usiamo la modalità WCS Priority
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
    // MODALITÀ 1: WCS PRIORITARIO (CORRETTA)
    // =======================================================================
    
    private async Task RunWcsPriorityModeAsync(
        List<FitsFileReference> files, 
        List<Point2D?> guesses, 
        Point2D?[] results, 
        IProgress<AlignmentProgressReport>? progress, 
        CancellationToken token)
    {
        // 1. CALCOLO DELL'OFFSET DI RIFERIMENTO (FRAME 0)
        // L'obiettivo è che il Frame 0 non si muova. Quindi il suo "centro di allineamento"
        // deve coincidere con il suo "centro geometrico".
        
        var header0 = await DataManager.GetHeaderOnlyAsync(files[0].FilePath);
        double w0 = _metadataService.GetIntValue(header0!, "NAXIS1");
        double h0 = _metadataService.GetIntValue(header0!, "NAXIS2");
        
        Point2D geoCenter0 = new Point2D(w0 / 2.0, h0 / 2.0);
        Point2D wcsPoint0 = guesses[0]!.Value; // Il punto WCS input (es. CRPIX o RA/DEC pixel)

        // Calcoliamo il vettore che porta dal punto WCS al centro geometrico
        double deltaX = geoCenter0.X - wcsPoint0.X;
        double deltaY = geoCenter0.Y - wcsPoint0.Y;

        // Fissiamo il risultato per il frame 0
        results[0] = geoCenter0;
        
        progress?.Report(new AlignmentProgressReport {
            CurrentIndex = 1, 
            TotalCount = files.Count,
            FileName = System.IO.Path.GetFileName(files[0].FilePath),
            FoundCenter = results[0], 
            Message = "Riferimento WCS fissato (Ancorato al centro geometrico)."
        });

        for (int i = 1; i < files.Count; i++)
        {
            token.ThrowIfCancellationRequested();

            // CASO A: Dato WCS presente per questo frame
            if (i < guesses.Count && guesses[i].HasValue)
            {
                // Applichiamo lo STESSO delta calcolato sul primo frame.
                // Logica: Se WcsPoint0 doveva spostarsi di Delta per finire al centro,
                // allora WcsPointN deve spostarsi dello stesso Delta (relativo al sistema di coordinate)
                // per mantenere l'allineamento relativo.
                
                Point2D wcsPointI = guesses[i]!.Value;
                results[i] = new Point2D(wcsPointI.X + deltaX, wcsPointI.Y + deltaY);
                
                progress?.Report(new AlignmentProgressReport {
                    CurrentIndex = i + 1,
                    TotalCount = files.Count,
                    FileName = System.IO.Path.GetFileName(files[i].FilePath),
                    FoundCenter = results[i],
                    Message = "Allineamento WCS (Relativo)..."
                });
            }
            // CASO B: Buco nei dati WCS -> Fallback su FFT relativa
            else
            {
                Point2D shift = await CalculateShiftWithRetryAsync(files[i - 1].FilePath, files[i].FilePath);
                
                // Qui usiamo results[i-1] che è già "corretto" con il delta, quindi la catena continua coerente
                Point2D prevCenter = results[i - 1] ?? new Point2D(0, 0);
                results[i] = new Point2D(prevCenter.X + shift.X, prevCenter.Y + shift.Y);
                
                progress?.Report(new AlignmentProgressReport {
                    CurrentIndex = i + 1,
                    TotalCount = files.Count,
                    FileName = System.IO.Path.GetFileName(files[i].FilePath),
                    FoundCenter = results[i],
                    Message = "Fallback FFT (WCS mancante)..."
                });
            }
        }
    }

    // =======================================================================
    // MODALITÀ 2: FFT PURA (VISUALE / BLIND) - INVARIATA
    // =======================================================================

    private async Task RunVisualBlindModeAsync(
        List<FitsFileReference> files, 
        Point2D?[] results, 
        IProgress<AlignmentProgressReport>? progress, 
        CancellationToken token)
    {
        var firstHeader = await DataManager.GetHeaderOnlyAsync(files[0].FilePath);
        double w = _metadataService.GetIntValue(firstHeader!, "NAXIS1");
        double h = _metadataService.GetIntValue(firstHeader!, "NAXIS2");
        results[0] = new Point2D(w / 2.0, h / 2.0);
        
        ReportProgress(progress, 0, files.Count, files[0].FilePath, results[0], "Riferimento geometrico fissato.");

        Mat? prevMat = await LoadMatWithRetryAsync(files[0].FilePath);
        if (prevMat == null) return; 

        try
        {
            for (int i = 1; i < files.Count; i++)
            {
                token.ThrowIfCancellationRequested();

                Mat? currentMat = await LoadMatWithRetryAsync(files[i].FilePath);

                if (currentMat != null)
                {
                    var shiftResult = await Task.Run(() => _analysis.ComputeStarFieldShift(prevMat, currentMat));
                    Point2D shift = shiftResult.Shift;

                    Point2D prevCenter = results[i - 1]!.Value;
                    results[i] = new Point2D(prevCenter.X + shift.X, prevCenter.Y + shift.Y);

                    prevMat.Dispose();       
                    prevMat = currentMat;    
                    currentMat = null;       
                }
                else
                {
                    results[i] = results[i - 1];
                }

                ReportProgress(progress, i, files.Count, files[i].FilePath, results[i], "Analisi FFT Campo Stellare...");
            }
        }
        finally
        {
            prevMat?.Dispose();
        }
    }

    // =======================================================================
    // HELPERS
    // =======================================================================

    private async Task<Mat?> LoadMatWithRetryAsync(string path)
    {
        return await ExecuteWithRetryAsync(async () =>
        {
            var data = await DataManager.GetDataAsync(path);
            double bScale = _metadataService.GetDoubleValue(data.Header, "BSCALE", 1.0);
            return _converter.RawToMat(data.PixelData, bScale);
        }, -1);
    }

    private async Task<Point2D> CalculateShiftWithRetryAsync(string pathPrev, string pathCurr)
    {
        return await ExecuteWithRetryAsync(async () =>
        {
            using var m1 = await LoadMatWithRetryAsync(pathPrev);
            using var m2 = await LoadMatWithRetryAsync(pathCurr);
            
            if (m1 == null || m2 == null) return (Point2D?)new Point2D(0, 0);

            var res = _analysis.ComputeStarFieldShift(m1, m2);
            return res.Shift;
        }, -1) ?? new Point2D(0, 0);
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