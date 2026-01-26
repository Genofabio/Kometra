using System;
using System.Threading.Tasks;
using OpenCvSharp;

namespace KomaLab.Services.Processing.Engines.Enhancement;

public interface IGradientRadialEngine
{
    // --- Metodi da StructureExtractionEngine (Rotational/Gradient) ---
    Task ApplyLarsonSekaninaAsync(Mat src, Mat dst, double angleDeg, double radialShiftX, double radialShiftY, bool isSymmetric);
    Task ApplyAdaptiveRVSFAsync(Mat src, Mat dst, double paramA, double paramB, double paramN, bool useLog, IProgress<double> progress = null);
    Task ApplyRVSFMosaicAsync(Mat src, Mat dst, (double v1, double v2) paramA, (double v1, double v2) paramB, (double v1, double v2) paramN, bool useLog);

    // --- Metodi da RadialEnhancementEngine (Polar Geometry) ---
    void ApplyInverseRho(Mat src, Mat dst, int subsampling = 5);
    Mat ToPolar(Mat src, int nRad, int nTheta, int subsampling = 5);
    void FromPolar(Mat polar, Mat dst, int width, int height, int subsampling = 5);
    void ApplyAzimuthalAverage(Mat polar, double rejSig);
    void ApplyAzimuthalMedian(Mat polar);
    void ApplyAzimuthalRenormalization(Mat polar, double rejSig, double nSig);
}