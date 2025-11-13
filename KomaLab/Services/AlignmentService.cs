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

    // Richiede il "laboratorio" (ImageProcessingService) per fare il lavoro
    public AlignmentService(IImageProcessingService processingService)
    {
        _processingService = processingService;
    }

    public Task<IEnumerable<Point?>> CalculateCentersAsync(
        AlignmentMode mode, 
        List<FitsImageData?> sourceData, // <-- Firma aggiornata
        IEnumerable<Point?> currentCoordinates, 
        int searchRadius)
    {
        if (mode != AlignmentMode.Manual)
        {
            Debug.WriteLine($"Modalità {mode} non ancora implementata, restituisco i click originali.");
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
                // 1. NON CARICA DA DISCO. Usa i dati passati.
                using Mat imageMat = _processingService.LoadFitsDataAsMat(fitsData);

                // 2. Definisci la ROI
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

                // 3. Esegui GetCenterByPeak
                Point localCenter = _processingService.GetCenterByPeak(regionCrop, sigma: 1.0);
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
    
    public async Task<List<FitsImageData>> ApplyCenteringAsync(List<FitsImageData?> sourceData, List<Point?>? centers)
    {
        // Esegui il processamento in parallelo
        var tasks = sourceData.Select((data, index) =>
        {
            if (data == null)
                return Task.FromResult<FitsImageData?>(null)!;

            var centerPoint = centers != null && index < centers.Count ? centers[index] : null;

            // Se non c'è un centro, restituisci i dati originali
            if (centerPoint == null)
                return Task.FromResult(data);

            // Se c'è un centro, applica lo shift
            try
            {
                using Mat originalMat = _processingService.LoadFitsDataAsMat(data);
                using Mat centeredMat = _processingService.CenterImageByCoords(originalMat, centerPoint.Value);
                // Crea un nuovo FitsImageData (processato)
                return Task.FromResult(_processingService.CreateFitsDataFromMat(centeredMat, data));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ApplyCentering fallito per l'immagine {index}: {ex.Message}");
                return Task.FromResult(data); // Fallback ai dati originali
            }
        });

        var results = await Task.WhenAll(tasks);
        return results.ToList();
    }

}