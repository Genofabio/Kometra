using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KomaLab.Models;
using Point = Avalonia.Point;

namespace KomaLab.Services.Imaging;

public interface IAlignmentService
{
    Task<IEnumerable<Point?>> CalculateCentersAsync(
        AlignmentTarget target, 
        AlignmentMode mode,
        CenteringMethod method,
        List<string> sourcePaths,
        IEnumerable<Point?> currentCoordinates,
        int searchRadius,
        IProgress<(int Index, Point? Center)>? progress = null);

    bool CanCalculate(AlignmentTarget target, AlignmentMode mode, IEnumerable<Point?> coords, int totalCount);
    
    Task<List<string>> ApplyCenteringAndSaveAsync(
        List<string> sourcePaths, 
        List<Point?> centers,
        string tempFolderPath);
}