using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KomaLab.Models;
using KomaLab.Models.Fits;
using KomaLab.Models.Processing;
using KomaLab.Services.Data;
using OpenCvSharp;
using Point = Avalonia.Point;
using Rect = Avalonia.Rect;
using Size = Avalonia.Size;

namespace KomaLab.Services.Imaging;

public class AlignmentService : IAlignmentService
{
    private readonly IFitsDataConverter _converter;
    private readonly IImageAnalysisService _analysis;
    private readonly IImageOperationService _operations;
    private readonly IFitsService _fitsService;
    
    public AlignmentService(
        IFitsDataConverter converter,
        IImageAnalysisService analysis,
        IImageOperationService operations,
        IFitsService fitsService)
    {
        _converter = converter;
        _analysis = analysis;
        _operations = operations;
        _fitsService = fitsService;
    }

    public async Task<IEnumerable<Point?>> CalculateCentersAsync(
        AlignmentTarget target, 
        AlignmentMode mode,
        CenteringMethod method,
        List<string> sourcePaths,
        IEnumerable<Point?> currentCoordinates,
        int searchRadius,
        IProgress<(int Index, Point? Center)>? progress = null)
    {
        var guesses = currentCoordinates.ToList();
        int n = sourcePaths.Count;
        Point?[] results = new Point?[n];

        // 1. Ottimizzazione Concorrenza (basata sulla dimensione file)
        long firstFileSize = 0;
        try { if (sourcePaths.Count > 0) firstFileSize = new System.IO.FileInfo(sourcePaths[0]).Length; } catch { }

        int maxConcurrency;
        if (firstFileSize > 100 * 1024 * 1024) maxConcurrency = 1; 
        else if (firstFileSize > 20 * 1024 * 1024) maxConcurrency = Math.Clamp(Environment.ProcessorCount / 2, 2, 3);
        else maxConcurrency = Math.Clamp(Environment.ProcessorCount, 2, 4);

        using var semaphore = new SemaphoreSlim(maxConcurrency);
        var processingTasks = new List<Task>();

        // ====================================================================
        // CASO A: ALLINEAMENTO STELLE (IBRIDO WCS + FFT FALLBACK)
        // ====================================================================
        if (target == AlignmentTarget.Stars)
        {
            // Se guesses[0] ha un valore, significa che il ViewModel ci ha passato dati WCS validi.
            // Entriamo in modalità "Fiducia nell'Input" con fallback FFT.
            bool hasWcsInput = (guesses.Count > 0 && guesses[0].HasValue);
            
            if (hasWcsInput)
            {
                // --- STRATEGIA 1: WCS PRIORITARIO ---
                results[0] = guesses[0];
                progress?.Report((0, results[0]));

                for (int i = 1; i < n; i++)
                {
                    if (i < guesses.Count && guesses[i].HasValue)
                    {
                        // CASO 1A: Dato WCS presente -> Usalo (Veloce)
                        results[i] = guesses[i];
                    }
                    else
                    {
                        // CASO 1B: Dato WCS mancante -> Calcolo visuale (FFT) sul precedente (Fallback)
                        try
                        {
                            var dataPrev = await _fitsService.LoadFitsFromFileAsync(sourcePaths[i - 1]);
                            var dataCurr = await _fitsService.LoadFitsFromFileAsync(sourcePaths[i]);

                            if (dataPrev != null && dataCurr != null)
                            {
                                using var matPrev = _converter.RawToMat(dataPrev);
                                using var matCurr = _converter.RawToMat(dataCurr);

                                Point shift = await Task.Run(() => _analysis.ComputeStarFieldShift(matPrev, matCurr));

                                Point prevCenter = results[i - 1] ?? new Point(0,0);
                                results[i] = new Point(prevCenter.X + shift.X, prevCenter.Y + shift.Y);
                            }
                            else
                            {
                                results[i] = results[i - 1]; 
                            }
                        }
                        catch 
                        { 
                            results[i] = results[i - 1]; 
                        }
                    }
                    progress?.Report((i, results[i]));
                }
                return results;
            }
            else
            {
                // --- STRATEGIA 2: FFT PURA (VISUALE) ---
                // 1. Inizializzazione Frame 0
                try 
                {
                    var data0 = await _fitsService.LoadFitsFromFileAsync(sourcePaths[0]);
                    if (data0 == null) return results;
                    results[0] = new Point(data0.Width / 2.0, data0.Height / 2.0);
                    progress?.Report((0, results[0]));
                }
                catch { return results; }

                // 2. Loop FFT Sequenziale
                Mat? prevMat = null;
                try 
                {
                    var data0 = await _fitsService.LoadFitsFromFileAsync(sourcePaths[0]);
                    if(data0 != null) prevMat = _converter.RawToMat(data0);
                } catch {}

                for (int i = 1; i < n; i++)
                {
                    try
                    {
                        var dataCurr = await _fitsService.LoadFitsFromFileAsync(sourcePaths[i]);
                        if (dataCurr != null && prevMat != null)
                        {
                            using var currentMat = _converter.RawToMat(dataCurr);

                            Point shift = await Task.Run(() => _analysis.ComputeStarFieldShift(prevMat, currentMat));

                            Point prevCenter = results[i - 1]!.Value;
                            results[i] = new Point(prevCenter.X + shift.X, prevCenter.Y + shift.Y);
                            
                            prevMat.Dispose();
                            prevMat = currentMat.Clone();
                        }
                        else
                        {
                            results[i] = results[i-1];
                        }
                        progress?.Report((i, results[i]));
                    }
                    catch
                    {
                        results[i] = results[i-1];
                    }
                }
                prevMat?.Dispose();
                return results;
            }
        }

        // ====================================================================
        // CASO B: ALLINEAMENTO COMETA
        // ====================================================================
        
        // --- STRATEGIA 3: GUIDATA (Smart Path da NASA o Lineare) ---
        if (mode == AlignmentMode.Guided)
        {
            var p1Guess = guesses.FirstOrDefault();
            var pNGuess = guesses.LastOrDefault();

            if (n < 2 || !p1Guess.HasValue || !pNGuess.HasValue) return guesses;

            Mat? templateMat = null;
            try
            {
                // 1. FRAME 0 (START)
                var data0 = await _fitsService.LoadFitsFromFileAsync(sourcePaths[0]);
                if (data0 == null) return guesses;

                var t0 = _operations.ExtractRefinedTemplate(data0, p1Guess.Value, searchRadius);
                templateMat = t0.template; 
                Point center1Precise = p1Guess.Value;
                    
                results[0] = center1Precise;
                progress?.Report((0, center1Precise));

                // 2. FRAME N (END)
                Point centerNPrecise = pNGuess.Value;
                results[n - 1] = centerNPrecise;
                progress?.Report((n - 1, centerNPrecise));

                double stepX = (centerNPrecise.X - center1Precise.X) / (n - 1);
                double stepY = (centerNPrecise.Y - center1Precise.Y) / (n - 1);

                var intermediateTasks = new List<Task>();
                for (int i = 1; i < n - 1; i++)
                {
                    int index = i;
                    string path = sourcePaths[index];

                    // CALCOLO CENTRO DI RICERCA (NASA vs Lineare)
                    Point expectedPoint;
                    if (index < guesses.Count && guesses[index].HasValue)
                    {
                        expectedPoint = guesses[index]!.Value;
                    }
                    else
                    {
                        double linX = center1Precise.X + (index * stepX);
                        double linY = center1Precise.Y + (index * stepY);
                        expectedPoint = new Point(linX, linY);
                    }

                    intermediateTasks.Add(Task.Run(async () =>
                    {
                        await semaphore.WaitAsync();
                        try
                        {
                            FitsImageData? fitsData = await _fitsService.LoadFitsFromFileAsync(path);
                            if (fitsData == null) { results[index] = null; return; }

                            using Mat fullImage = _converter.RawToMat(fitsData);

                            Point? foundMatch = _operations.FindTemplatePosition(fullImage, templateMat, expectedPoint, searchRadius);
                                
                            var finalPoint = foundMatch ?? expectedPoint;
                            results[index] = finalPoint;
                            progress?.Report((index, finalPoint));
                        }
                        catch
                        {
                            results[index] = expectedPoint;
                            progress?.Report((index, expectedPoint));
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    }));
                }
                await Task.WhenAll(intermediateTasks);
            }
            finally
            {
                templateMat?.Dispose();
            }
            return results;
        }    
        // --- STRATEGIA 4: AUTOMATICA / MANUALE (Con supporto ROI NASA) ---
        else 
        {
            for (int i = 0; i < n; i++)
            {
                int index = i;
                string path = sourcePaths[index];
                
                var guessPoint = (index < guesses.Count) ? guesses[index] : null;

                processingTasks.Add(Task.Run(async () =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        Point? result = await AttemptCalculationWithRetryAsync(
                            index, path, guessPoint, mode, method, searchRadius
                        );
                        results[index] = result;
                        progress?.Report((index, result));
                    }
                    finally { semaphore.Release(); }
                }));
            }
            await Task.WhenAll(processingTasks);
            return results;
        }
    }
    
    // ====================================================================
    // METODI HELPER
    // ====================================================================

    private async Task<Point?> AttemptCalculationWithRetryAsync(
        int index, string path, Point? guessPoint,
        AlignmentMode mode, CenteringMethod method, int searchRadius)
    {
        int attempts = 0;
        while (true)
        {
            attempts++;
            FitsImageData? fitsData;
            try
            {
                fitsData = await _fitsService.LoadFitsFromFileAsync(path);
                if (fitsData == null) return null;

                using Mat fullImageMat = _converter.RawToMat(fitsData);

                if (attempts > 1) { GC.Collect(); await Task.Delay(50); }

                Point? calculatedCenter;

                // DECISIONE: ROI vs FULL IMAGE
                bool hasGuess = guessPoint.HasValue;
                bool useRestrictedSearch = hasGuess && searchRadius > 0;

                if (mode == AlignmentMode.Automatic && !useRestrictedSearch)
                {
                    // --- CASO BLIND ---
                    calculatedCenter = _analysis.FindCenterOfLocalRegion(fullImageMat);
                }
                else
                {
                    // --- CASO ROI (NASA/Manuale) ---
                    if (guessPoint == null) return null;

                    int size = searchRadius * 2;
                    int x = (int)(guessPoint.Value.X - searchRadius);
                    int y = (int)(guessPoint.Value.Y - searchRadius);
                    
                    Rect imageBounds = new Rect(0, 0, fullImageMat.Width, fullImageMat.Height);
                    Rect targetRect = new Rect(x, y, size, size);
                    Rect roiRect = imageBounds.Intersect(targetRect);

                    if (roiRect.Width <= 4 || roiRect.Height <= 4)
                    {
                        calculatedCenter = guessPoint;
                    }
                    else
                    {
                        var cvRoi = new OpenCvSharp.Rect((int)roiRect.X, (int)roiRect.Y, (int)roiRect.Width, (int)roiRect.Height);
                        using Mat regionCrop = new Mat(fullImageMat, cvRoi);
                        Point localCenter;

                        switch (method)
                        {
                            case CenteringMethod.Centroid: localCenter = _analysis.FindCentroid(regionCrop); break;
                            case CenteringMethod.GaussianFit: localCenter = _analysis.FindGaussianCenter(regionCrop); break;
                            case CenteringMethod.Peak: localCenter = _analysis.FindPeak(regionCrop); break;
                            default: localCenter = _analysis.FindCenterOfLocalRegion(regionCrop); break;
                        }
                        
                        calculatedCenter = new Point(localCenter.X + roiRect.X, localCenter.Y + roiRect.Y);
                    }
                }

                return calculatedCenter;
            }
            catch (Exception)
            {
                if (attempts >= 3) return guessPoint; 
                await Task.Delay(200 * attempts);
            }
        }
    }
    
    // Calcola il rettangolo minimo (Bounding Box)
    private async Task<(Size Size, Point ShiftCorrection)> CalculateUnionBoundingBoxAsync(
        List<string> paths, 
        List<Point?> centers)
    {
        double minLeft = double.MaxValue;
        double minTop = double.MaxValue;
        double maxRight = double.MinValue;
        double maxBottom = double.MinValue;

        bool hasData = false;

        for (int i = 0; i < paths.Count; i++)
        {
            if (i >= centers.Count || centers[i] == null) continue;
            Point c = centers[i]!.Value;

            var header = await _fitsService.ReadHeaderOnlyAsync(paths[i]);
            if (header != null)
            {
                hasData = true;
                double w = header.GetIntValue("NAXIS1");
                double h = header.GetIntValue("NAXIS2");

                double relLeft = -c.X;
                double relTop = -c.Y;
                double relRight = w - c.X;
                double relBottom = h - c.Y;

                if (relLeft < minLeft) minLeft = relLeft;
                if (relTop < minTop) minTop = relTop;
                if (relRight > maxRight) maxRight = relRight;
                if (relBottom > maxBottom) maxBottom = relBottom;
            }
        }

        if (!hasData) return (new Size(100, 100), new Point(0, 0));

        double totalW = Math.Ceiling(maxRight - minLeft);
        double totalH = Math.Ceiling(maxBottom - minTop);
        
        double idealCenterX = -minLeft; 
        double idealCenterY = -minTop;  
        double canvasCenterX = totalW / 2.0;
        double canvasCenterY = totalH / 2.0;

        double shiftX = canvasCenterX - idealCenterX;
        double shiftY = canvasCenterY - idealCenterY;

        return (new Size(totalW, totalH), new Point(shiftX, shiftY));
    }
    
    public async Task<List<string>> ApplyCenteringAndSaveAsync(
        List<string> sourcePaths,
        List<Point?> centers,
        string tempFolderPath,
        AlignmentTarget target) 
    {
        Size finalSize;
        Point offsetCorrection = new Point(0, 0);

        // 1. CALCOLO DIMENSIONI
        if (target == AlignmentTarget.Stars)
        {
            var result = await CalculateUnionBoundingBoxAsync(sourcePaths, centers);
            finalSize = result.Size;
            offsetCorrection = result.ShiftCorrection;
        }
        else
        {
            finalSize = await CalculatePerfectCanvasSizeAsync(sourcePaths, centers);
        }

        long firstFileSize = 0;
        try { if (sourcePaths.Count > 0) firstFileSize = new System.IO.FileInfo(sourcePaths[0]).Length; } catch { }
        int maxConcurrency = (firstFileSize > 100 * 1024 * 1024) ? 1 : Math.Clamp(Environment.ProcessorCount / 2, 2, 3);

        using var semaphore = new SemaphoreSlim(maxConcurrency);
        var tasks = new List<Task<string?>>();

        if (!System.IO.Directory.Exists(tempFolderPath))
            System.IO.Directory.CreateDirectory(tempFolderPath);

        for (int i = 0; i < sourcePaths.Count; i++)
        {
            string path = sourcePaths[i];
            var center = centers[i];
            int index = i;

            if (center == null) continue;

            Point adjustedCenter = center.Value + offsetCorrection;

            tasks.Add(Task.Run(async () =>
            {
                await semaphore.WaitAsync();
                try
                {
                    return await AttemptProcessAndSaveWithRetryAsync(
                        index, path, adjustedCenter, finalSize, tempFolderPath
                    );
                }
                finally { semaphore.Release(); }
            }));
        }

        var results = await Task.WhenAll(tasks);
        return results.Where(p => p != null).Cast<string>().ToList();
    }

    private async Task<string?> AttemptProcessAndSaveWithRetryAsync(
        int index, string path, Point? center, Size targetSize, string tempFolderPath)
    {
        if (center == null) return null;
        int attempts = 0;
        while (true)
        {
            attempts++;
            FitsImageData? inputData;
            try
            {
                inputData = await _fitsService.LoadFitsFromFileAsync(path);
                if (inputData == null) return null;
                if (attempts > 1) { GC.Collect(); await Task.Delay(100); }

                FitsImageData outputData = await Task.Run(() =>
                {
                    using Mat originalMat = _converter.RawToMat(inputData);
                    using Mat centeredMat = _operations.GetSubPixelCenteredCanvas(originalMat, center.Value, targetSize);
                    return _converter.MatToFitsData(centeredMat, inputData);
                });

                string fileName = $"Aligned_{index}_{Guid.NewGuid()}.fits";
                string fullPath = System.IO.Path.Combine(tempFolderPath, fileName);
                await _fitsService.SaveFitsFileAsync(outputData, fullPath);

                return fullPath;
            }
            catch
            {
                if (attempts >= 3) return null;
                await Task.Delay(300 * attempts);
            }
        }
    }
    
    private async Task<Size> CalculatePerfectCanvasSizeAsync(List<string> paths, List<Point?> centers)
    {
        double maxRadiusX = 0;
        double maxRadiusY = 0;

        for (int i = 0; i < paths.Count; i++)
        {
            if (i >= centers.Count || centers[i] == null) continue;
            Point center = centers[i]!.Value;

            var data = await _fitsService.LoadFitsFromFileAsync(paths[i]);
            if (data != null)
            {
                using Mat mat = _converter.RawToMat(data);
                Rect validBox = _analysis.FindValidDataBox(mat);

                if (validBox.Width > 0 && validBox.Height > 0)
                {
                    double distLeft = center.X - validBox.X;
                    double distRight = (validBox.X + validBox.Width) - center.X;
                    double distTop = center.Y - validBox.Y;
                    double distBottom = (validBox.Y + validBox.Height) - center.Y;
                    double myRadiusX = Math.Max(distLeft, distRight);
                    double myRadiusY = Math.Max(distTop, distBottom);
                    if (myRadiusX > maxRadiusX) maxRadiusX = myRadiusX;
                    if (myRadiusY > maxRadiusY) maxRadiusY = myRadiusY;
                }
            }
        }
        int finalW = (int)Math.Ceiling(maxRadiusX * 2);
        int finalH = (int)Math.Ceiling(maxRadiusY * 2);
        return (finalW > 0 && finalH > 0) ? new Size(finalW, finalH) : new Size(100, 100);
    }

    public bool CanCalculate(AlignmentTarget target, AlignmentMode mode, IEnumerable<Point?> currentCoordinates, int totalCount)
    {
        if (totalCount == 0) return false;

        var coordinateList = currentCoordinates.ToList();
        
        if (target == AlignmentTarget.Stars) return true;

        switch (mode)
        {
            case AlignmentMode.Automatic: 
                return true; 
            
            case AlignmentMode.Guided:
                if (totalCount <= 1) return coordinateList.Count > 0 && coordinateList[0].HasValue;
                var hasFirst = coordinateList.Count > 0 && coordinateList[0].HasValue;
                var hasLast = coordinateList.Count >= totalCount && coordinateList[totalCount - 1].HasValue;
                return hasFirst && hasLast;
            
            case AlignmentMode.Manual: 
                return coordinateList.Count == totalCount && coordinateList.All(e => e.HasValue);
            
            default: return false;
        }
    }
}