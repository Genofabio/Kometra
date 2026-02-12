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

    // --- BORDI & DETTAGLI ---
    /// <summary>
    /// Applica il filtro di Laplace adattivo (SNN Smoothing + Laplace Kernel) per estrarre strutture fini riducendo il rumore.
    /// </summary>
    Task ApplyAdaptiveLaplaceAsync(Mat src, Mat dst, IProgress<double> progress = null);
}