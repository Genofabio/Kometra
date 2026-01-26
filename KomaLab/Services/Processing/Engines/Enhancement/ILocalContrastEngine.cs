using System;
using System.Threading.Tasks;
using OpenCvSharp;

namespace KomaLab.Services.Processing.Engines.Enhancement;

public interface ILocalContrastEngine
{
    // --- UNSHARP MASKING ---
    Task ApplyUnsharpMaskingMedianAsync(Mat src, Mat dst, int kernelSize, IProgress<double> progress = null);
    
    // --- LOCAL NORMALIZATION ---
    Task ApplyLocalNormalizationAsync(Mat src, Mat dst, int windowSize, double intensity, IProgress<double> progress = null);
    
    // --- CLAHE ---
    Task ApplyClaheAsync(Mat src, Mat dst, double clipLimit, int tileGridSize);
}