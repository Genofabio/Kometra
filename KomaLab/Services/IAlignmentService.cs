using System.Collections.Generic;
using System.Threading.Tasks;
using KomaLab.Models;
using Point = Avalonia.Point;

namespace KomaLab.Services;

public interface IAlignmentService
{
    Task<IEnumerable<Point?>> CalculateCentersAsync(
        AlignmentMode mode, 
        CenteringMethod method,
        List<FitsImageData?> sourceData, 
        IEnumerable<Point?> currentCoordinates, 
        int searchRadius);

    bool CanCalculate(
        AlignmentMode mode, 
        IEnumerable<Point?> currentCoordinates, 
        int totalCount);
    
    Task<List<FitsImageData?>> ApplyCenteringAsync(
        List<FitsImageData?> sourceData,
        List<Point?> centers);
}