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

    // --- Metodi da RadialEnhancementEngine (Polar Geometry & Digital Models) ---
    
    // Inverse Rho (1/r) - Filtro base geometrico
    void ApplyInverseRho(Mat src, Mat dst, int subsampling = 5);

    // R.W.M. (Radial Weighted Model) - Filtro geometrico (Sottrae background e moltiplica per R)
    // Aggiornato con maxFilterRadius per il blending
    void ApplyRadialWeightedModel(Mat src, Mat dst, double backgroundValue, int maxFilterRadius = 0);

    // M.C.M. (Median Coma Model) - Filtro statistico (Sottrae modello mediano)
    void ApplyMedianComaModel(Mat src, Mat dst, int maxFilterRadius = 0, int subsampling = 5);

    // --- Metodi di Supporto Polare ---
    Mat ToPolar(Mat src, int nRad, int nTheta, int subsampling = 5);
    void FromPolar(Mat polar, Mat dst, int width, int height, int subsampling = 5);
    void ApplyAzimuthalAverage(Mat polar, double rejSig);
    void ApplyAzimuthalMedian(Mat polar);
    void ApplyAzimuthalRenormalization(Mat polar, double rejSig, double nSig);
}