using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KomaLab.Models;
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
    // Ottimizzazione preliminare
    if (target == AlignmentTarget.Comet && searchRadius <= 0 && 
       (mode == AlignmentMode.Manual || mode == AlignmentMode.Guided))
    {
        return currentCoordinates;
    }

    var guesses = currentCoordinates.ToList();
    int n = sourcePaths.Count;
    Point?[] results = new Point?[n];

    long firstFileSize = 0;
    try { if (sourcePaths.Count > 0) firstFileSize = new System.IO.FileInfo(sourcePaths[0]).Length; } catch { }

    int maxConcurrency;
    if (firstFileSize > 100 * 1024 * 1024) maxConcurrency = 1; 
    else if (firstFileSize > 20 * 1024 * 1024) maxConcurrency = Math.Clamp(Environment.ProcessorCount / 2, 2, 3);
    else maxConcurrency = Math.Clamp(Environment.ProcessorCount, 2, 4);

    using var semaphore = new SemaphoreSlim(maxConcurrency);
    var processingTasks = new List<Task>();

    // ====================================================================
    // CASO A: ALLINEAMENTO STELLE
    // ====================================================================
    if (target == AlignmentTarget.Stars)
    {
        // MODIFICA QUI: Se abbiamo punti in input (dal WCS), usiamo quelli!
        // Controlliamo se la lista guesses è valida e piena
        if (guesses != null && guesses.Count == n && guesses.All(g => g.HasValue))
        {
            // Copia diretta. L'astrometria ha già calcolato tutto.
            for(int i=0; i<n; i++) 
            {
                results[i] = guesses[i];
                progress?.Report((i, guesses[i]));
            }
            return results;
        }

        // --- FALLBACK: ALLINEAMENTO VISUALE (FFT) ---
        // (Tutto il codice vecchio con ComputeStarFieldShift rimane qui come else)
        if (n < 2) return guesses;
        Point masterCenter;
        Mat? prevMat = null;

        try
        {
            var data0 = await _fitsService.LoadFitsFromFileAsync(sourcePaths[0]);
            if (data0 == null) return results;

            masterCenter = new Point(data0.Width / 2.0, data0.Height / 2.0);
            results[0] = masterCenter;
            progress?.Report((0, masterCenter));

            prevMat = _converter.RawToMat(data0);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Errore Init Master Stars: {ex.Message}");
            prevMat?.Dispose();
            return guesses;
        }

        double totalShiftX = 0;
        double totalShiftY = 0;

        for (int i = 1; i < n; i++)
        {
            int index = i;
            string path = sourcePaths[index];
            Mat? currentMat = null;

            try
            {
                var dataTarget = await _fitsService.LoadFitsFromFileAsync(path);
                if (dataTarget == null) { results[index] = null; continue; }

                currentMat = _converter.RawToMat(dataTarget);
    
                Point stepShift = await Task.Run(() => 
                    _analysis.ComputeStarFieldShift(prevMat, currentMat)
                );

                totalShiftX += stepShift.X;
                totalShiftY += stepShift.Y;

                Point newCenter = new Point(
                    masterCenter.X + totalShiftX,
                    masterCenter.Y + totalShiftY
                );

                results[index] = newCenter;
                progress?.Report((index, newCenter)); 

                prevMat?.Dispose(); 
                prevMat = currentMat; 
                currentMat = null; 
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Errore StarAlign img {index}: {ex.Message}");
                results[index] = null;
            }
            finally
            {
                currentMat?.Dispose();
            }
        }

        prevMat?.Dispose();
        return results;
    }

    // ====================================================================
    // CASO B: ALLINEAMENTO COMETA
    // ====================================================================
    
    // --- STRATEGIA: GUIDATA (Interpolazione Lineare) ---
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

            // --- MODIFICA 1: NON RICALCOLARE IL CENTRO INIZIALE ---
            // Usiamo p1Guess.Value direttamente come centro preciso.
            // Estraiamo il template *attorno* a quel punto fissato dall'utente/NASA.
            var t0 = _operations.ExtractRefinedTemplate(data0, p1Guess.Value, searchRadius);
            templateMat = t0.template; 
            Point center1Precise = p1Guess.Value; // <--- FIDUCIA CIECA
                
            results[0] = center1Precise;
            progress?.Report((0, center1Precise));

            // 2. FRAME N (END)
            var dataN = await _fitsService.LoadFitsFromFileAsync(sourcePaths[n - 1]);
            if (dataN == null) return guesses;
                
            // --- MODIFICA 2: NON RICALCOLARE IL CENTRO FINALE ---
            Point centerNPrecise = pNGuess.Value; // <--- FIDUCIA CIECA
                
            results[n - 1] = centerNPrecise;
            progress?.Report((n - 1, centerNPrecise));

            // Parametri per fallback lineare (se mancano dati intermedi)
            double stepX = (centerNPrecise.X - center1Precise.X) / (n - 1);
            double stepY = (centerNPrecise.Y - center1Precise.Y) / (n - 1);

            var intermediateTasks = new List<Task>();
            for (int i = 1; i < n - 1; i++)
            {
                int index = i;
                string path = sourcePaths[index];

                // --- MODIFICA 3: USARE IL PUNTO "SMART" CALCOLATO NEL VIEWMODEL ---
                Point expectedPoint;
                if (index < guesses.Count && guesses[index].HasValue)
                {
                    // Se il ViewModel ci ha passato un punto (calcolato con la curva NASA), usiamo quello!
                    expectedPoint = guesses[index]!.Value;
                }
                else
                {
                    // Altrimenti interpolazione lineare classica
                    double linX = center1Precise.X + (i * stepX);
                    double linY = center1Precise.Y + (i * stepY);
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

                        // FindTemplatePosition userà expectedPoint come centro della ROI.
                        // Se expectedPoint segue la curva NASA, la ROI seguirà la cometa perfettamente!
                        Point? foundMatch = _operations.FindTemplatePosition(fullImage, templateMat, expectedPoint, searchRadius);
                            
                        var finalPoint = foundMatch ?? expectedPoint;
                        results[index] = finalPoint;
                        progress?.Report((index, finalPoint));
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[Guided] Error Img {index}: {ex.Message}");
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
        catch (Exception ex)
        {
            Debug.WriteLine($"[Guided] Fatal Error: {ex.Message}");
            return guesses;
        }
        finally
        {
            templateMat?.Dispose();
        }
        return results;
    }    
    // --- STRATEGIA: AUTOMATICA (Con o Senza NASA) & MANUALE ---
    else 
    {
        for (int i = 0; i < n; i++)
        {
            int index = i;
            string path = sourcePaths[index];
            var guessPoint = guesses[index];

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
    // METODI HELPER (RETRY & SAVE)
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

                // =========================================================
                // MODIFICA CRUCIALE PER GESTIRE "AUTOMATICO + NASA"
                // =========================================================
                
                // Definiamo se usare il "Search Box" ristretto.
                // Succede in 2 casi:
                // 1. Manuale (guessPoint è il click utente)
                // 2. Automatico (guessPoint è la coordinata NASA)
                bool useRestrictedSearch = guessPoint.HasValue && searchRadius > 0;

                // Caso speciale: Automatico PURO (nessuna NASA, nessun click)
                if (mode == AlignmentMode.Automatic && !useRestrictedSearch)
                {
                    // Vecchio comportamento: Cerca su tutta l'immagine
                    calculatedCenter = _analysis.FindCenterOfLocalRegion(fullImageMat);
                }
                else
                {
                    // Caso: Automatico+NASA oppure Manuale
                    // Qui usiamo il guessPoint e il searchRadius per fare il crop
                    if (guessPoint == null) return null;

                    int size = searchRadius * 2;
                    int x = (int)(guessPoint.Value.X - searchRadius);
                    int y = (int)(guessPoint.Value.Y - searchRadius);
                    
                    Rect imageBounds = new Rect(0, 0, fullImageMat.Width, fullImageMat.Height);
                    Rect targetRect = new Rect(x, y, size, size);
                    Rect roiRect = imageBounds.Intersect(targetRect);

                    if (roiRect.Width <= 0 || roiRect.Height <= 0)
                    {
                        // Se la ROI è fuori immagine, restituiamo il punto originale (NASA o Manuale)
                        calculatedCenter = guessPoint;
                    }
                    else
                    {
                        var cvRoi = new OpenCvSharp.Rect((int)roiRect.X, (int)roiRect.Y, (int)roiRect.Width, (int)roiRect.Height);
                        using Mat regionCrop = new Mat(fullImageMat, cvRoi);
                        Point localCenter;

                        // Applichiamo l'algoritmo scelto SOLO sulla ROI
                        switch (method)
                        {
                            case CenteringMethod.Centroid: localCenter = _analysis.FindCentroid(regionCrop); break;
                            case CenteringMethod.GaussianFit: localCenter = _analysis.FindGaussianCenter(regionCrop); break;
                            case CenteringMethod.Peak: localCenter = _analysis.FindPeak(regionCrop); break;
                            // Default per Automatico+NASA usiamo "LocalRegion" che è robusto per le comete
                            default: localCenter = _analysis.FindCenterOfLocalRegion(regionCrop); break;
                        }
                        
                        // Sommiamo l'offset della ROI per avere coordinate globali
                        calculatedCenter = new Point(localCenter.X + roiRect.X, localCenter.Y + roiRect.Y);
                    }
                }

                return calculatedCenter;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Retry System] Fallito tentativo {attempts} su Img {index}: {ex.Message}");
                if (attempts >= 3) return guessPoint; // Fallback al punto originale in caso di errore
                await Task.Delay(200 * attempts);
            }
        }
    }
    
    public async Task<List<string>> ApplyCenteringAndSaveAsync(
        List<string> sourcePaths,
        List<Point?> centers,
        string tempFolderPath)
    {
        Size perfectSize = await CalculatePerfectCanvasSizeAsync(sourcePaths, centers);
        long firstFileSize = 0;
        try { if (sourcePaths.Count > 0) firstFileSize = new System.IO.FileInfo(sourcePaths[0]).Length; } catch { }

        int maxConcurrency;
        if (firstFileSize > 100 * 1024 * 1024) maxConcurrency = 1;
        else maxConcurrency = Math.Clamp(Environment.ProcessorCount / 2, 2, 3);

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

            tasks.Add(Task.Run(async () =>
            {
                await semaphore.WaitAsync();
                try
                {
                    return await AttemptProcessAndSaveWithRetryAsync(
                        index, path, center, perfectSize, tempFolderPath
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
            FitsImageData? outputData;
            try
            {
                inputData = await _fitsService.LoadFitsFromFileAsync(path);
                if (inputData == null) return null;
                if (attempts > 1) { GC.Collect(); await Task.Delay(100); }

                outputData = await Task.Run(() =>
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
            catch (Exception ex)
            {
                Debug.WriteLine($"[Save Retry] Fallito tentativo {attempts} su Img {index}: {ex.Message}");
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
            if (centers[i] == null) continue;
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
        var coordinateList = currentCoordinates.ToList();
        if (coordinateList.Count == 0) return false;

        if (target == AlignmentTarget.Stars) return true;

        switch (mode)
        {
            case AlignmentMode.Automatic: 
                return true; 
            
            case AlignmentMode.Guided:
                if (totalCount <= 1) return coordinateList[0].HasValue;
                var hasFirst = coordinateList.FirstOrDefault().HasValue;
                var hasLast = coordinateList.LastOrDefault().HasValue;
                return (hasFirst && hasLast) || coordinateList.All(e => e.HasValue);
            
            case AlignmentMode.Manual: 
                return coordinateList.All(e => e.HasValue);
            
            default: return false;
        }
    }
}