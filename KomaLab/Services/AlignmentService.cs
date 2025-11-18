using KomaLab.ViewModels; 
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using KomaLab.Models;
using OpenCvSharp;
using Point = Avalonia.Point;

namespace KomaLab.Services;

/// <summary>
/// Implementazione del servizio di logica di business dell'allineamento.
/// Orchestra il processo chiamando altri servizi (es. ImageProcessing).
/// </summary>
public class AlignmentService : IAlignmentService
{
    private readonly IImageProcessingService _processingService;
    
    public AlignmentService(IImageProcessingService processingService)
    {
        _processingService = processingService;
    }

    public async Task<IEnumerable<Point?>> CalculateCentersAsync(
        AlignmentMode mode, 
        CenteringMethod method, 
        List<FitsImageData?> sourceData, 
        IEnumerable<Point?> currentCoordinates, 
        int searchRadius)
    {
        if (mode != AlignmentMode.Manual || searchRadius <= 0)
        {
            return currentCoordinates;
        }

        var guesses = currentCoordinates.ToList();
        
        var processingTasks = new List<Task<Point?>>();
        for (int i = 0; i < sourceData.Count; i++)
        {
            int index = i;
            var guessPoint = guesses[index];
            var fitsData = sourceData[index];

            processingTasks.Add(Task.Run(() =>
            {
                if (guessPoint == null || fitsData == null)
                {
                    return (Point?)null; 
                }
                
                try
                {
                    using Mat fullImageMat = _processingService.LoadFitsDataAsMat(fitsData);
                    
                    int size = searchRadius * 2;
                    int x = (int)(guessPoint.Value.X - searchRadius);
                    int y = (int)(guessPoint.Value.Y - searchRadius);
                    
                    int safeX = Math.Max(0, x);
                    int safeY = Math.Max(0, y);
                    int safeW = Math.Min(size, fullImageMat.Width - safeX);
                    int safeH = Math.Min(size, fullImageMat.Height - safeY);
                    
                    if (safeW <= 0 || safeH <= 0)
                    {
                        return guessPoint.Value; // Fallback
                    }
                    
                    Rect roiRect = new Rect(safeX, safeY, safeW, safeH);
                    using Mat regionCrop = new Mat(fullImageMat, roiRect);
                    
                    Point localCenter;
                    switch (method)
                    {
                        case CenteringMethod.Centroid:
                            localCenter = _processingService.GetCenterByCentroid(regionCrop);
                            break;

                        case CenteringMethod.GaussianFit:
                            localCenter = _processingService.GetCenterByGaussianFit(regionCrop);
                            break;

                        case CenteringMethod.Peak:
                            localCenter = _processingService.GetCenterByPeak(regionCrop);
                            break;
                        
                        default:
                            localCenter = _processingService.GetCenterOfLocalRegion(regionCrop);
                            break;
                    }
                    
                    Point globalCenter = new Point(localCenter.X + safeX, localCenter.Y + safeY);
                    return globalCenter;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Calcolo fallito per l'immagine {index}: {ex.Message}");
                    return guessPoint.Value;
                }
            }));
        }
        
        var newCoordinates = await Task.WhenAll(processingTasks);
        return newCoordinates;
    }

    public bool CanCalculate(
        AlignmentMode mode, 
        IEnumerable<Point?> currentCoordinates, 
        int totalCount)
    {
        var coordinateList = currentCoordinates.ToList();
        if (coordinateList.Count == 0)
            return false;
        
        switch (mode)
        {
            case AlignmentMode.Automatic:
                return true; 

            case AlignmentMode.Guided:
                if (totalCount == 1)
                {
                    return coordinateList[0].HasValue;
                }
                
                var hasFirstAndLast = coordinateList.FirstOrDefault().HasValue && 
                                      coordinateList.LastOrDefault().HasValue;
                
                var hasAllGuided = coordinateList.All(e => e.HasValue);
                return hasFirstAndLast || hasAllGuided;

            case AlignmentMode.Manual:
                var hasAllManual = coordinateList.All(e => e.HasValue);
                return hasAllManual;

            default:
                return false;
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
                using Mat originalMat = _processingService.LoadFitsDataAsMat(data);
                using Mat centeredMat = _processingService.GetSubPixelCenteredCanvas(originalMat, centerPoint.Value, targetSize);
                return _processingService.CreateFitsDataFromMat(centeredMat, data);
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
            
            using Mat mat = _processingService.LoadFitsDataAsMat(data);
            Rect validBox = _processingService.FindValidDataBox(mat);

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