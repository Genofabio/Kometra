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

    public Task<IEnumerable<Point?>> CalculateCentersAsync(
        AlignmentMode mode, 
        List<FitsImageData?> sourceData, 
        IEnumerable<Point?> currentCoordinates, 
        int searchRadius)
    {
        if (mode != AlignmentMode.Manual)
        {
            return Task.FromResult(currentCoordinates);
        }

        var newCoordinates = new List<Point?>();
        var guesses = currentCoordinates.ToList();

        for (int i = 0; i < sourceData.Count; i++)
        {
            var guessPoint = guesses[i];
            var fitsData = sourceData[i];

            if (guessPoint == null || fitsData == null)
            {
                newCoordinates.Add(null);
                continue; 
            }
            
            try
            {
                using Mat imageMat = _processingService.LoadFitsDataAsMat(fitsData);

                // Definisci la ROI
                int x = (int)(guessPoint.Value.X - searchRadius);
                int y = (int)(guessPoint.Value.Y - searchRadius);
                int size = searchRadius * 2;
                
                int xCrop = Math.Max(0, x);
                int yCrop = Math.Max(0, y);
                int wCrop = Math.Min(size, imageMat.Width - xCrop);
                int hCrop = Math.Min(size, imageMat.Height - yCrop);
                
                if (wCrop <= 0 || hCrop <= 0)
                {
                    newCoordinates.Add(guessPoint.Value); // Fallback
                    continue;
                }
                
                Rect roiRect = new Rect(xCrop, yCrop, wCrop, hCrop);
                using Mat regionCrop = new Mat(imageMat, roiRect);
                
                // Calcola il bounding box che esclude i NaN (usando la tua funzione)
                Rect validBox = _processingService.FindValidDataBox(regionCrop);
                using Mat validCrop = new Mat(regionCrop, validBox);
                Point centerInCrop = _processingService.GetCenterByPeak(validCrop, sigma: 1.0);
                
                Point localCenter = new Point(centerInCrop.X + validBox.X, centerInCrop.Y + validBox.Y);
                Point globalCenter = new Point(localCenter.X + xCrop, localCenter.Y + yCrop);
                
                newCoordinates.Add(globalCenter);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Calcolo fallito per l'immagine {i}: {ex.Message}");
                newCoordinates.Add(guessPoint.Value); // Fallback
            }
        }
        
        return Task.FromResult<IEnumerable<Point?>>(newCoordinates);
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
            using Mat centeredMat = GetSubPixelCenteredCanvas(originalMat, centerPoint.Value, targetSize);
            return _processingService.CreateFitsDataFromMat(centeredMat, data);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error img {index}: {ex.Message}");
            return data;
        }
    });
}

    /// <summary>
    /// Esegue Centratura + Padding in un colpo solo con precisione SubPixel.
    /// </summary>
    private Mat GetSubPixelCenteredCanvas(Mat source, Point originalCenter, Size outputSize)
    {
        double destCenterX = outputSize.Width / 2.0;
        double destCenterY = outputSize.Height / 2.0;
        
        double tx = destCenterX - originalCenter.X;
        double ty = destCenterY - originalCenter.Y;
        
        using Mat m = new Mat(2, 3, MatType.CV_32F);
        m.Set(0, 0, 1.0f); m.Set(0, 1, 0.0f); m.Set(0, 2, (float)tx);
        m.Set(1, 0, 0.0f); m.Set(1, 1, 1.0f); m.Set(1, 2, (float)ty);
        
        Mat result = new Mat(outputSize, source.Type(), new Scalar(double.NaN));
        
        Cv2.WarpAffine(
            source, 
            result, 
            m, 
            outputSize, 
            InterpolationFlags.Linear, 
            BorderTypes.Transparent, 
            new Scalar(double.NaN)
        );

        return result;
    }

}