using System;
using System.Threading.Tasks;
using OpenCvSharp;

namespace KomaLab.Services.Processing.Engines.Enhancement;

public interface IStructureShapeEngine
{
    // --- FILAMENTI & FLUSSI ---
    Task ApplyFrangiVesselnessAsync(Mat src, Mat dst, double sigma, double beta, double c, IProgress<double> progress = null);
    Task ApplyStructureTensorEnhancementAsync(Mat src, Mat dst, int sigma, int rho, IProgress<double> progress = null);
    
    // --- MORFOLOGIA ---
    Task ApplyWhiteTopHatAsync(Mat src, Mat dst, int kernelSize);
}