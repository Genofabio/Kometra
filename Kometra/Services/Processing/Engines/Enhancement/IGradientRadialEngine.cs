using System;
using System.Threading.Tasks;
using OpenCvSharp;

namespace Kometra.Services.Processing.Engines.Enhancement;

public interface IGradientRadialEngine
{
    Task ApplyLarsonSekaninaAsync(Mat src, Mat dst, double angleDeg, double radialShiftX, double radialShiftY, bool isSymmetric);
    Task ApplyAdaptiveRVSFAsync(Mat src, Mat dst, double paramA, double paramB, double paramN, bool useLog, IProgress<double> progress = null);
    Task ApplyRVSFMosaicAsync(Mat src, Mat dst, (double v1, double v2) paramA, (double v1, double v2) paramB, (double v1, double v2) paramN, bool useLog);
    void ApplyInverseRho(Mat src, Mat dst);
    void ApplyRadialWeightedModel(Mat src, Mat dst, double maxFilterRadius = 0.0);
    void ApplyMedianComaModel(Mat src, Mat dst, double maxFilterRadius = 0.0, int angularQuality = 5);
    Mat ToPolar(Mat src, int nRad, int nTheta);
    void FromPolar(Mat polar, Mat dst, int width, int height);
    void ApplyAzimuthalAverage(Mat polar, double rejSig);
    void ApplyAzimuthalMedian(Mat polar);
    void ApplyAzimuthalRenormalization(Mat polar, double rejSig, double nSig);
}