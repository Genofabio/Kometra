using System;
using System.Threading.Tasks;
using OpenCvSharp;

namespace KomaLab.Services.Processing.Engines.Enhancement;

public interface IGradientRadialEngine
{
    // --- Metodi da StructureExtractionEngine ---
    Task ApplyLarsonSekaninaAsync(Mat src, Mat dst, double angleDeg, double radialShiftX, double radialShiftY, bool isSymmetric);
    Task ApplyAdaptiveRVSFAsync(Mat src, Mat dst, double paramA, double paramB, double paramN, bool useLog, IProgress<double> progress = null);
    Task ApplyRVSFMosaicAsync(Mat src, Mat dst, (double v1, double v2) paramA, (double v1, double v2) paramB, (double v1, double v2) paramN, bool useLog);

    // --- Metodi da RadialEnhancementEngine ---
    
    // Inverse Rho (1/r)
    void ApplyInverseRho(Mat src, Mat dst);

    // R.W.M. - Ora accetta double per il raggio
    void ApplyRadialWeightedModel(Mat src, Mat dst, double maxFilterRadius = 0.0);

    // M.C.M. - Ora accetta double per il raggio
    void ApplyMedianComaModel(Mat src, Mat dst, double maxFilterRadius = 0.0, int angularQuality = 5);

    // --- Metodi di Supporto Polare ---
    Mat ToPolar(Mat src, int nRad, int nTheta);
    void FromPolar(Mat polar, Mat dst, int width, int height);
    
    // --- Metodi Azimutali ---
    void ApplyAzimuthalAverage(Mat polar, double rejSig);
    void ApplyAzimuthalMedian(Mat polar);
    void ApplyAzimuthalRenormalization(Mat polar, double rejSig, double nSig);
}