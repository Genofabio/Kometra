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

/// <summary>
/// Strategia di allineamento guidato (Cometa).
/// Supporta traiettoria densa (JPL) o interpolazione visiva intelligente (Start/End manuali + Star Tracking FFT).
/// </summary>
public class GuidedCometAlignmentStrategy : AlignmentStrategyBase
{
    private readonly IFitsMetadataService _metadataService;
    private readonly IFitsOpenCvConverter _converter;
    private readonly IImageAnalysisEngine _analysis;

    public GuidedCometAlignmentStrategy(
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
        var inputList = guesses.ToList();
        var results = new Point2D?[fileList.Count];

        // 1. ANALISI INPUT
        int validPoints = inputList.Count(x => x.HasValue);
        // Se abbiamo molti punti (>2), assumiamo siano dati JPL o simili -> Dense Trajectory
        bool isDenseTrajectory = validPoints > 2; 

        int firstIndex = inputList.FindIndex(g => g.HasValue);
        if (firstIndex == -1) return results;

        // 2. RAFFINAMENTO START (Anchor Point)
        // Calcoliamo subito il punto di partenza reale
        Point2D startEstimate = inputList[firstIndex]!.Value;
        Point2D startRefined = await RefineCenterAsync(fileList[firstIndex], startEstimate, searchRadius) 
                               ?? startEstimate;
        results[firstIndex] = startRefined;

        int lastIndex = inputList.FindLastIndex(g => g.HasValue);
        Point2D endEstimate = (lastIndex != -1) ? inputList[lastIndex]!.Value : startEstimate;

        // Limite di concorrenza calcolato in base alla RAM disponibile
        int concurrencyLimit = GetOptimalConcurrency(fileList[0]);

        // =========================================================================================
        // FASE A: PRE-CALCOLO TRAIETTORIA VISIVA (Solo se non abbiamo dati densi/JPL)
        // =========================================================================================
        Point2D[] starDriftPath = null;
        Point2D cometProperMotionTotal = new Point2D(0, 0);

        if (!isDenseTrajectory && lastIndex > firstIndex)
        {
            progress?.Report(new AlignmentProgressReport { Message = "Calcolo tracking stellare..." });

            int steps = lastIndex - firstIndex;
            var shiftTasks = new Task<Point2D>[steps];

            // 1. Usiamo un semaforo anche per questa fase per evitare picchi di memoria
            using var shiftSemaphore = new SemaphoreSlim(concurrencyLimit);

            for (int k = 0; k < steps; k++)
            {
                int idxA = firstIndex + k;
                int idxB = firstIndex + k + 1;
                
                // Avvolgiamo la chiamata nel semaforo
                shiftTasks[k] = Task.Run(async () => 
                {
                    await shiftSemaphore.WaitAsync(token);
                    try
                    {
                        return await CalculateShiftWithRetryAsync(fileList[idxA], fileList[idxB]);
                    }
                    finally
                    {
                        shiftSemaphore.Release();
                    }
                }, token);
            }

            var shifts = await Task.WhenAll(shiftTasks);

            // 2. Accumulo del percorso stellare (Star Path)
            starDriftPath = new Point2D[fileList.Count];
            double accX = 0, accY = 0;
            
            // Il frame Start ha drift 0
            starDriftPath[firstIndex] = new Point2D(0, 0); 

            for (int k = 0; k < shifts.Length; k++)
            {
                accX += shifts[k].X;
                accY += shifts[k].Y;
                starDriftPath[firstIndex + k + 1] = new Point2D(accX, accY);
            }

            // 3. Calcolo del vettore "Moto Proprio Cometa" (Closing Error)
            Point2D starOnlyEndPos = new Point2D(startRefined.X + accX, startRefined.Y + accY);
            
            cometProperMotionTotal = new Point2D(
                endEstimate.X - starOnlyEndPos.X,
                endEstimate.Y - starOnlyEndPos.Y
            );
        }

        // =========================================================================================
        // FASE B: ESECUZIONE RAFFINAMENTO (Loop Parallelo)
        // =========================================================================================
        using var refinementSemaphore = new SemaphoreSlim(concurrencyLimit);
        
        var processingTasks = fileList.Select(async (fileRef, i) =>
        {
            // Saltiamo i frame fuori dal range definito dall'utente
            if (i < firstIndex || i > lastIndex) return; 
            if (i == firstIndex) return; // Già fatto

            await refinementSemaphore.WaitAsync(token);
            try
            {
                token.ThrowIfCancellationRequested();
                Point2D estimatedPos;

                if (isDenseTrajectory)
                {
                    // --- MODALITÀ 1: DATA-DRIVEN (JPL) ---
                    if (inputList[i].HasValue)
                    {
                        double deltaX = inputList[i]!.Value.X - startEstimate.X;
                        double deltaY = inputList[i]!.Value.Y - startEstimate.Y;
                        estimatedPos = new Point2D(startRefined.X + deltaX, startRefined.Y + deltaY);
                    }
                    else return;
                }
                else
                {
                    // --- MODALITÀ 2: VISUAL TRACKING (Interpolazione Smart) ---
                    double progressRatio = (double)(i - firstIndex) / (lastIndex - firstIndex);
                    
                    Point2D starShift = starDriftPath![i]; 
                    double cometShiftX = cometProperMotionTotal.X * progressRatio;
                    double cometShiftY = cometProperMotionTotal.Y * progressRatio;

                    estimatedPos = new Point2D(
                        startRefined.X + starShift.X + cometShiftX,
                        startRefined.Y + starShift.Y + cometShiftY
                    );
                }

                // 4. RAFFINAMENTO LOCALE
                results[i] = await ExecuteWithRetryAsync(
                    async () => await RefineCenterAsync(fileRef, estimatedPos, searchRadius),
                    i
                ) ?? estimatedPos;

                progress?.Report(new AlignmentProgressReport { 
                    CurrentIndex = i + 1, 
                    TotalCount = fileList.Count, 
                    FileName = System.IO.Path.GetFileName(fileRef.FilePath),
                    FoundCenter = results[i], 
                    Message = isDenseTrajectory ? "Tracking JPL..." : "Tracking Visivo..." 
                });
            }
            finally { refinementSemaphore.Release(); }
        });

        await Task.WhenAll(processingTasks);
        return results;
    }

    // =======================================================================
    // HELPERS (Copiati/Adattati per supportare il calcolo shift)
    // =======================================================================

    private async Task<Point2D> CalculateShiftWithRetryAsync(FitsFileReference prev, FitsFileReference curr)
    {
        var result = await ExecuteWithRetryAsync<Point2D?>(async () =>
        {
            using var m1 = await LoadMatForFftAsync(prev);
            using var m2 = await LoadMatForFftAsync(curr);
        
            if (m1 == null || m2 == null) return null;

            var res = _analysis.ComputeStarFieldShift(m1, m2);
            return res.Shift;
        }, -1);

        return result ?? new Point2D(0, 0);
    }

    private async Task<Mat?> LoadMatForFftAsync(FitsFileReference fileRef)
    {
        var data = await DataManager.GetDataAsync(fileRef.FilePath);
        var header = fileRef.ModifiedHeader ?? data.Header;
        double bScale = _metadataService.GetDoubleValue(header, "BSCALE", 1.0);
        double bZero = _metadataService.GetDoubleValue(header, "BZERO", 0.0);

        // Usiamo targetBitDepth = null per lasciare che il convertitore decida (o 32 o 64)
        // PatchNaNsInPlace (ereditato) gestirà entrambi i casi grazie a SafePatchNaNs
        var mat = _converter.RawToMat(data.PixelData, bScale, bZero);
        
        PatchNaNsInPlace(mat);
        return mat;
    }

    private async Task<Point2D?> RefineCenterAsync(FitsFileReference fileRef, Point2D guess, int radius)
    {
        var data = await DataManager.GetDataAsync(fileRef.FilePath);
        var header = fileRef.ModifiedHeader ?? data.Header;
        double bScale = _metadataService.GetDoubleValue(header, "BSCALE", 1.0);
        double bZero = _metadataService.GetDoubleValue(header, "BZERO", 0.0);
        
        using var fullMat = _converter.RawToMat(data.PixelData, bScale, bZero);
        
        // SanitizeAndCrop (ereditato) è safe per i double
        var (workMat, offset) = SanitizeAndCrop(fullMat, guess);

        using (workMat)
        {
            Point2D localGuess = new Point2D(guess.X - offset.X, guess.Y - offset.Y);
            
            if (localGuess.X < 0 || localGuess.Y < 0 || 
                localGuess.X >= workMat.Width || localGuess.Y >= workMat.Height)
                return guess;

            var roiRect = new Rect(
                (int)(localGuess.X - radius), 
                (int)(localGuess.Y - radius), 
                radius * 2, radius * 2
            ).Intersect(new Rect(0, 0, workMat.Width, workMat.Height));

            if (roiRect.Width <= 4 || roiRect.Height <= 4) return guess;

            using var roiMat = new Mat(workMat, roiRect);
            var localCenter = _analysis.FindCenterOfLocalRegion(roiMat);
            
            return new Point2D(
                localCenter.X + roiRect.X + offset.X, 
                localCenter.Y + roiRect.Y + offset.Y
            );
        }
    }
}