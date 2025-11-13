using KomaLab.ViewModels; 
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Point = Avalonia.Point;
using Size = Avalonia.Size;

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

    #region Logica di Business (Interfaccia Principale)

    public async Task<IEnumerable<Point?>> CalculateCentersAsync(
        AlignmentMode mode, 
        IEnumerable<Point?> currentCoordinates, 
        Size imageSize)
    {
        var newCoordinates = currentCoordinates.ToList();
        
        // TODO: Questa è ancora la logica di simulazione/placeholder.
        // In futuro, questa logica chiamerà _processingService.GetCenterOfLocalRegion, etc.
        await Task.Delay(500); 
        Random rand = new Random();

        if (mode == AlignmentMode.Automatic)
        {
            for(int i = 0; i < newCoordinates.Count; i++)
            {
                double x = rand.NextDouble() * imageSize.Width;
                double y = rand.NextDouble() * imageSize.Height;
                newCoordinates[i] = new Point(x, y);
            }
        }
        else if (mode == AlignmentMode.Guided)
        {
            for(int i = 0; i < newCoordinates.Count; i++)
            {
                if (newCoordinates[i] == null)
                {
                    double x = rand.NextDouble() * imageSize.Width;
                    double y = rand.NextDouble() * imageSize.Height;
                    newCoordinates[i] = new Point(x, y);
                }
            }
        }
        
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
                
                bool hasFirstAndLast = coordinateList.FirstOrDefault().HasValue && 
                                       coordinateList.LastOrDefault().HasValue;
                
                bool hasAllGuided = coordinateList.All(e => e.HasValue);
                return hasFirstAndLast || hasAllGuided;

            case AlignmentMode.Manual:
                bool hasAllManual = coordinateList.All(e => e.HasValue);
                return hasAllManual;

            default:
                return false;
        }
    }

    #endregion
}