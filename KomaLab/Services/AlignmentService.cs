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
    // --- DIPENDENZE AGGIORNATE (SRP) ---
    private readonly IFitsDataConverter _converter;
    private readonly IImageAnalysisService _analysis;
    private readonly IImageOperationService _operations;
    
    public AlignmentService(
        IFitsDataConverter converter,
        IImageAnalysisService analysis,
        IImageOperationService operations)
    {
        _converter = converter;
        _analysis = analysis;
        _operations = operations;
    }

    public async Task<IEnumerable<Point?>> CalculateCentersAsync(
        AlignmentMode mode, 
        CenteringMethod method, 
        List<FitsImageData?> sourceData, 
        IEnumerable<Point?> currentCoordinates, 
        int searchRadius)
    {
        if (searchRadius <= 0 && (mode == AlignmentMode.Manual || mode == AlignmentMode.Guided))
        {
            return currentCoordinates;
        }

        var guesses = currentCoordinates.ToList();
        int n = sourceData.Count;
        Point?[] results = new Point?[n]; 
        
        int maxConcurrency = Math.Min(4, Environment.ProcessorCount);
        using var semaphore = new SemaphoreSlim(maxConcurrency); 
        var processingTasks = new List<Task>();

        // ====================================================================
        // --- STRATEGIA 1: MANUALE ---
        // ====================================================================
        if (mode == AlignmentMode.Manual)
        {
            for (int i = 0; i < n; i++)
            {
                int index = i; 
                var guessPoint = guesses[index];
                var fitsData = sourceData[index];

                processingTasks.Add(Task.Run(async () =>
                {
                    await semaphore.WaitAsync(); 
                    try
                    {
                        if (guessPoint == null || fitsData == null) 
                        {
                            results[index] = null;
                            return;
                        }

                        // USA IL CONVERTER
                        using Mat fullImageMat = _converter.RawToMat(fitsData);
                        
                        int size = searchRadius * 2;
                        int x = (int)(guessPoint.Value.X - searchRadius);
                        int y = (int)(guessPoint.Value.Y - searchRadius);
                        
                        // Rettangoli Avalonia (Double) per logica intersection
                        Rect imageBounds = new Rect(0, 0, fullImageMat.Width, fullImageMat.Height);
                        Rect targetRect = new Rect(x, y, size, size);
                        Rect roiRect = imageBounds.Intersect(targetRect);

                        if (roiRect.Width <= 0 || roiRect.Height <= 0)
                        {
                            results[index] = guessPoint; 
                            return;
                        }

                        // CAST INT per OpenCV (usando i valori validati dalla logica rect)
                        var cvRoi = new OpenCvSharp.Rect((int)roiRect.X, (int)roiRect.Y, (int)roiRect.Width, (int)roiRect.Height);
                        using Mat regionCrop = new Mat(fullImageMat, cvRoi);
                        Point localCenter;

                        // USA IL SERVIZIO DI ANALISI (Nomi metodi aggiornati)
                        switch (method)
                        {
                            case CenteringMethod.Centroid:
                                localCenter = _analysis.FindCentroid(regionCrop);
                                break;
                            case CenteringMethod.GaussianFit:
                                localCenter = _analysis.FindGaussianCenter(regionCrop);
                                break;
                            case CenteringMethod.Peak:
                                localCenter = _analysis.FindPeak(regionCrop);
                                break;
                            default: 
                                localCenter = _analysis.FindCenterOfLocalRegion(regionCrop);
                                break;
                        }

                        results[index] = new Point(localCenter.X + roiRect.X, localCenter.Y + roiRect.Y);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[Manual] Err img {index}: {ex.Message}");
                        results[index] = guessPoint;
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }));
            }
            
            await Task.WhenAll(processingTasks);
            return results;
        }

        // ====================================================================
        // --- STRATEGIA 2: AUTOMATICA ---
        // ====================================================================
        else if (mode == AlignmentMode.Automatic)
        {
            for (int i = 0; i < n; i++)
            {
                int index = i;
                var fitsData = sourceData[index];

                processingTasks.Add(Task.Run(async () =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        if (fitsData == null) 
                        {
                            results[index] = null;
                            return;
                        }

                        // USA IL CONVERTER
                        using Mat fullImageMat = _converter.RawToMat(fitsData);
                        
                        // USA IL SERVIZIO DI ANALISI
                        Point center = _analysis.FindCenterOfLocalRegion(fullImageMat);
                        results[index] = center;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[Auto] Err img {index}: {ex.Message}");
                        results[index] = null;
                    }
                    finally
                    {
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

            if (n < 2 || !p1Guess.HasValue || !pNGuess.HasValue || sourceData[0] == null || sourceData[n - 1] == null)
                return guesses;

            Mat? templateMat = null;

            try
            {
                // USA IL SERVIZIO OPERAZIONI per il template
                var t0 = _operations.ExtractRefinedTemplate(sourceData[0], p1Guess.Value, searchRadius);
                templateMat = t0.template;
                Point center1Precise = t0.preciseCenter;
                results[0] = center1Precise; 

                var tN = _operations.ExtractRefinedTemplate(sourceData[n - 1], pNGuess.Value, searchRadius);
                Point centerNPrecise = tN.preciseCenter;
                results[n - 1] = centerNPrecise;
                tN.template.Dispose(); 

                double stepX = (centerNPrecise.X - center1Precise.X) / (n - 1);
                double stepY = (centerNPrecise.Y - center1Precise.Y) / (n - 1);

                var intermediateTasks = new List<Task>();
                
                for (int i = 1; i < n - 1; i++)
                {
                    int index = i;
                    var fitsData = sourceData[index];
                    
                    double guessX = center1Precise.X + (i * stepX);
                    double guessY = center1Precise.Y + (i * stepY);
                    Point expectedPoint = new Point(guessX, guessY);

                    intermediateTasks.Add(Task.Run(async () =>
                    {
                        await semaphore.WaitAsync();
                        try
                        {
                            if (fitsData == null) 
                            {
                                results[index] = null;
                                return;
                            }

                            // USA IL CONVERTER
                            using Mat fullImage = _converter.RawToMat(fitsData);
                            
                            // USA IL SERVIZIO OPERAZIONI per il match
                            Point? foundMatch = _operations.FindTemplatePosition(
                                fullImage, 
                                templateMat, 
                                expectedPoint, 
                                searchRadius
                            );

                            results[index] = foundMatch ?? expectedPoint;
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[Guided] Crash Img {index}: {ex.Message}");
                            results[index] = expectedPoint;
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

        return guesses;
    }

    public bool CanCalculate(
        AlignmentMode mode, 
        IEnumerable<Point?> currentCoordinates, 
        int totalCount)
    {
        var coordinateList = currentCoordinates.ToList();
        if (coordinateList.Count == 0) return false;
        
        switch (mode)
        {
            case AlignmentMode.Automatic:
                return true; 

            case AlignmentMode.Guided:
                if (totalCount == 1) return coordinateList[0].HasValue;
                
                var hasFirstAndLast = coordinateList.FirstOrDefault().HasValue && 
                                      coordinateList.LastOrDefault().HasValue;
                return hasFirstAndLast || coordinateList.All(e => e.HasValue);

            case AlignmentMode.Manual:
                return coordinateList.All(e => e.HasValue);

            default: return false;
        }
    }
    
    public async Task<List<FitsImageData?>> ApplyCenteringAsync(List<FitsImageData?> sourceData, List<Point?> centers)
    {
        Size perfectSize = CalculatePerfectCanvasSize(sourceData, centers);
        
        var tasks = sourceData.Select((data, index) => 
            ProcessSingleImageAsync(data, centers, index, perfectSize)
        );
        
        var results = await Task.WhenAll(tasks);
        return results.ToList();
    }

    private Task<FitsImageData?> ProcessSingleImageAsync(FitsImageData? data, List<Point?>? centers, int index, Size targetSize)
    {
        return Task.Run(() =>
        {
            if (data == null) return null;
            var centerPoint = (centers != null && index < centers.Count) ? centers[index] : null;
            if (centerPoint == null) return data;

            try
            {
                // USA CONVERTER e OPERATIONS
                using Mat originalMat = _converter.RawToMat(data);
                using Mat centeredMat = _operations.GetSubPixelCenteredCanvas(originalMat, centerPoint.Value, targetSize);
                
                return _converter.MatToFitsData(centeredMat, data);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error img {index}: {ex.Message}");
                return data;
            }
        });
    }
    
    private Size CalculatePerfectCanvasSize(List<FitsImageData?> sourceData, List<Point?> centers)
    {
        double maxRadiusX = 0;
        double maxRadiusY = 0;

        for (int i = 0; i < sourceData.Count; i++)
        {
            var data = sourceData[i];
            if (data == null || centers == null || i >= centers.Count || centers[i] == null) continue;

            Point center = centers[i]!.Value;
            
            // USA CONVERTER e ANALYSIS
            using Mat mat = _converter.RawToMat(data);
            Rect validBox = _analysis.FindValidDataBox(mat);

            if (validBox.Width <= 0 || validBox.Height <= 0) continue;
            
            double distLeft = center.X - validBox.X;
            double distRight = (validBox.X + validBox.Width) - center.X;
            double distTop = center.Y - validBox.Y;
            double distBottom = (validBox.Y + validBox.Height) - center.Y;
            double myRadiusX = Math.Max(distLeft, distRight);
            double myRadiusY = Math.Max(distTop, distBottom);
            
            if (myRadiusX > maxRadiusX) maxRadiusX = myRadiusX;
            if (myRadiusY > maxRadiusY) maxRadiusY = myRadiusY;
        }
        
        int finalW = (int)Math.Ceiling(maxRadiusX * 2);
        int finalH = (int)Math.Ceiling(maxRadiusY * 2);
        
        return (finalW > 0 && finalH > 0) ? new Size(finalW, finalH) : new Size(100, 100);
    }
}