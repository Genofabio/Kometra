using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KomaLab.Models;
using OpenCvSharp;
using Point = Avalonia.Point;
using Rect = Avalonia.Rect;
using Size = Avalonia.Size;

namespace KomaLab.Services;

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
        AlignmentMode mode, 
        CenteringMethod method, 
        List<string> sourcePaths, 
        IEnumerable<Point?> currentCoordinates, 
        int searchRadius)
    {
        if (searchRadius <= 0 && (mode == AlignmentMode.Manual || mode == AlignmentMode.Guided))
        {
            return currentCoordinates;
        }

        var guesses = currentCoordinates.ToList();
        int n = sourcePaths.Count;
        Point?[] results = new Point?[n]; 
        
        // Controlla la concorrenza per non intasare la RAM con troppe Matrici
        int maxConcurrency = Math.Min(4, Environment.ProcessorCount);
        using var semaphore = new SemaphoreSlim(maxConcurrency); 
        var processingTasks = new List<Task>();

        // ====================================================================
        // --- STRATEGIA 1 & 2: MANUALE / AUTOMATICA ---
        // ====================================================================
        if (mode == AlignmentMode.Manual || mode == AlignmentMode.Automatic)
        {
            for (int i = 0; i < n; i++)
            {
                int index = i;
                string path = sourcePaths[index];
                var guessPoint = guesses[index];

                processingTasks.Add(Task.Run(async () =>
                {
                    await semaphore.WaitAsync();
                    FitsImageData? fitsData = null; // Dichiarato fuori per il finally
                    
                    try
                    {
                        // 1. CARICA RAW DATA (RAM sale)
                        fitsData = await _fitsService.LoadFitsFromFileAsync(path);
                        if (fitsData == null) { results[index] = null; return; }

                        // 2. CONVERTI & LIBERA RAW DATA SUBITO
                        using Mat fullImageMat = _converter.RawToMat(fitsData);
                        fitsData = null; // Rende l'array C# liberabile
                        
                        // 3. CALCOLA
                        if (mode == AlignmentMode.Automatic)
                        {
                            results[index] = _analysis.FindCenterOfLocalRegion(fullImageMat);
                        }
                        else // Manual
                        {
                            if (guessPoint == null) { results[index] = null; return; }
                            
                            // Logica ROI (Identica alla tua)
                            int size = searchRadius * 2;
                            int x = (int)(guessPoint.Value.X - searchRadius);
                            int y = (int)(guessPoint.Value.Y - searchRadius);
                            
                            Rect imageBounds = new Rect(0, 0, fullImageMat.Width, fullImageMat.Height);
                            Rect targetRect = new Rect(x, y, size, size);
                            Rect roiRect = imageBounds.Intersect(targetRect);

                            if (roiRect.Width <= 0 || roiRect.Height <= 0) { results[index] = guessPoint; return; }

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
                            results[index] = new Point(localCenter.X + roiRect.X, localCenter.Y + roiRect.Y);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Err img {index}: {ex.Message}");
                        results[index] = (mode == AlignmentMode.Manual) ? guessPoint : null;
                    }
                    finally 
                    { 
                        // Assicurati che l'oggetto Raw Data sia nullo se è sopravvissuto all'eccezione
                        if (fitsData != null) fitsData = null;
                        semaphore.Release(); 
                    }
                }));
            }
            await Task.WhenAll(processingTasks);
            return results;
        }

        // ====================================================================
        // --- STRATEGIA 3: GUIDATA ---
        // ====================================================================
        else if (mode == AlignmentMode.Guided)
        {
            var p1Guess = guesses.FirstOrDefault();
            var pNGuess = guesses.LastOrDefault();

            if (n < 2 || !p1Guess.HasValue || !pNGuess.HasValue) return guesses;

            Mat? templateMat = null;

            try
            {
                // 1. Load Start & End (Necessario per Template/Traiettoria)
                
                var data0 = await _fitsService.LoadFitsFromFileAsync(sourcePaths[0]);
                if (data0 == null) return guesses;
                var t0 = _operations.ExtractRefinedTemplate(data0, p1Guess.Value, searchRadius);
                templateMat = t0.template;
                Point center1Precise = t0.preciseCenter;
                results[0] = center1Precise;
                data0 = null; // Libera RAM

                var dataN = await _fitsService.LoadFitsFromFileAsync(sourcePaths[n - 1]);
                if (dataN == null) return guesses;
                var tN = _operations.ExtractRefinedTemplate(dataN, pNGuess.Value, searchRadius);
                Point centerNPrecise = tN.preciseCenter;
                results[n - 1] = centerNPrecise;
                tN.template.Dispose(); 
                dataN = null; // Libera RAM

                // 2. Calcola traiettoria
                double stepX = (centerNPrecise.X - center1Precise.X) / (n - 1);
                double stepY = (centerNPrecise.Y - center1Precise.Y) / (n - 1);

                // 3. Elaborazione parallela dei frame intermedi
                var intermediateTasks = new List<Task>();
                
                for (int i = 1; i < n - 1; i++)
                {
                    int index = i;
                    string path = sourcePaths[index];
                    
                    double guessX = center1Precise.X + (i * stepX);
                    double guessY = center1Precise.Y + (i * stepY);
                    Point expectedPoint = new Point(guessX, guessY);

                    intermediateTasks.Add(Task.Run(async () =>
                    {
                        await semaphore.WaitAsync();
                        FitsImageData? fitsData = null;

                        try
                        {
                            fitsData = await _fitsService.LoadFitsFromFileAsync(path);
                            if (fitsData == null) { results[index] = null; return; }

                            using Mat fullImage = _converter.RawToMat(fitsData);
                            fitsData = null; // Libera RAM

                            Point? foundMatch = _operations.FindTemplatePosition(fullImage, templateMat, expectedPoint, searchRadius);
                            results[index] = foundMatch ?? expectedPoint;
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[Guided] Crash Img {index}: {ex.Message}");
                            results[index] = expectedPoint;
                        }
                        finally 
                        { 
                            if (fitsData != null) fitsData = null;
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

        return guesses;
    }
    
    // --- APPLICAZIONE (Con calcolo canvas perfetto) ---
    public async Task<List<string>> ApplyCenteringAndSaveAsync(
        List<string> sourcePaths, 
        List<Point?> centers,
        string tempFolderPath)
    {
        // 1. Calcola dimensione canvas ottimale (Metodo recuperato!)
        //    (Deve essere async perché carica i file uno a uno)
        Size perfectSize = await CalculatePerfectCanvasSizeAsync(sourcePaths, centers);
        
        int maxConcurrency = Math.Min(4, Environment.ProcessorCount);
        using var semaphore = new SemaphoreSlim(maxConcurrency);
        var tasks = new List<Task<string?>>();

        if (!System.IO.Directory.Exists(tempFolderPath))
             System.IO.Directory.CreateDirectory(tempFolderPath);

        for (int i = 0; i < sourcePaths.Count; i++)
        {
            string path = sourcePaths[i];
            var center = centers[i];
            int index = i;

            tasks.Add(Task.Run(async () =>
            {
                await semaphore.WaitAsync();
                try
                {
                    if (center == null) return null;

                    // Load
                    var data = await _fitsService.LoadFitsFromFileAsync(path);
                    if (data == null) return null;

                    // Process
                    var processedData = await ProcessSingleImageInternalAsync(data, center, perfectSize);
                    
                    // Free input
                    data = null; 

                    if (processedData == null) return null;

                    // Save
                    string fileName = $"Aligned_{index}_{Guid.NewGuid()}.fits";
                    string fullPath = System.IO.Path.Combine(tempFolderPath, fileName);
                    await _fitsService.SaveFitsFileAsync(processedData, fullPath);

                    return fullPath;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Errore salvataggio {index}: {ex.Message}");
                    return null;
                }
                finally { semaphore.Release(); }
            }));
        }

        var results = await Task.WhenAll(tasks);
        return results.Where(p => p != null).Cast<string>().ToList();
    }

    private async Task<FitsImageData?> ProcessSingleImageInternalAsync(FitsImageData data, Point? center, Size targetSize)
    {
        if (center == null) return null;
        
        return await Task.Run(() => 
        {
            using Mat originalMat = _converter.RawToMat(data);
            using Mat centeredMat = _operations.GetSubPixelCenteredCanvas(originalMat, center.Value, targetSize);
            return _converter.MatToFitsData(centeredMat, data);
        });
    }
    
    // METODO RECUPERATO E ADATTATO (ASYNC PER LOW RAM)
    private async Task<Size> CalculatePerfectCanvasSizeAsync(List<string> paths, List<Point?> centers)
    {
        double maxRadiusX = 0;
        double maxRadiusY = 0;

        for (int i = 0; i < paths.Count; i++)
        {
            if (centers[i] == null) continue;
            Point center = centers[i]!.Value;

            // Load -> Measure -> Drop
            var data = await _fitsService.LoadFitsFromFileAsync(paths[i]);
            if (data != null)
            {
                using Mat mat = _converter.RawToMat(data);
                
                // Usa la tua logica originale per trovare il box valido
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
            data = null; // Libera RAM
        }
        
        int finalW = (int)Math.Ceiling(maxRadiusX * 2);
        int finalH = (int)Math.Ceiling(maxRadiusY * 2);
        
        return (finalW > 0 && finalH > 0) ? new Size(finalW, finalH) : new Size(100, 100);
    }

    public bool CanCalculate(AlignmentMode mode, IEnumerable<Point?> currentCoordinates, int totalCount)
    {
        var coordinateList = currentCoordinates.ToList();
        if (coordinateList.Count == 0) return false;
        
        switch (mode)
        {
            case AlignmentMode.Automatic: return true; 
            case AlignmentMode.Guided:
                if (totalCount == 1) return coordinateList[0].HasValue;
                var hasFirstAndLast = coordinateList.FirstOrDefault().HasValue && coordinateList.LastOrDefault().HasValue;
                return hasFirstAndLast || coordinateList.All(e => e.HasValue);
            case AlignmentMode.Manual: return coordinateList.All(e => e.HasValue);
            default: return false;
        }
    }
}