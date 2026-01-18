using System;
using KomaLab.Models.Visualization;
using KomaLab.Services.Processing.Engines;
using OpenCvSharp;

namespace KomaLab.Services.Processing.Rendering;

public class ImagePresentationService : IImagePresentationService
{
    private readonly IRadiometryEngine _radiometry;

    public ImagePresentationService(IRadiometryEngine radiometry)
    {
        _radiometry = radiometry ?? throw new ArgumentNullException(nameof(radiometry));
    }

    // =======================================================================
    // 1. RENDERING (Stretching 8-bit)
    // =======================================================================

    public void RenderTo8Bit(Mat src, Mat dst, double blackPoint, double whitePoint, VisualizationMode mode)
    {
        if (src == null || src.Empty()) return;

        double range = whitePoint - blackPoint;
        if (Math.Abs(range) < 1e-9) range = 1.0;

        using Mat temp32F = new Mat();
        
        double alpha = 1.0 / range;
        double beta = -blackPoint * alpha;
        src.ConvertTo(temp32F, MatType.CV_32FC1, alpha, beta);

        Cv2.Max(temp32F, 0.0, temp32F);
        Cv2.Min(temp32F, 1.0, temp32F);

        ApplyTransferFunction(temp32F, mode);

        temp32F.ConvertTo(dst, MatType.CV_8UC1, 255.0);
    }

    private void ApplyTransferFunction(Mat mat, VisualizationMode mode)
    {
        switch (mode)
        {
            case VisualizationMode.SquareRoot: Cv2.Sqrt(mat, mat); break;
            case VisualizationMode.Logarithmic:
                Cv2.Add(mat, 1.0, mat);
                Cv2.Log(mat, mat);
                Cv2.Multiply(mat, 1.442695, mat); 
                break;
        }
    }

    // =======================================================================
    // 2. LOGICA DI PRESENTAZIONE (Requirements & Profiles)
    // =======================================================================

    public (double Mean, double StdDev) GetPresentationRequirements(Mat source)
    {
        if (source == null || source.Empty()) return (0, 1);
        // Delega la matematica pura al RadiometryEngine
        return _radiometry.ComputeStatistics(source);
    }

    public AbsoluteContrastProfile GetInitialProfile(Mat source)
    {
        return _radiometry.CalculateAutoStretchProfile(source);
    }

    public AbsoluteContrastProfile GetAdaptedProfile(
        Mat nextMat, 
        AbsoluteContrastProfile currentProfile, 
        (double Mean, double StdDev) currentMetrics)
    {
        if (nextMat == null || nextMat.Empty()) return currentProfile;

        // Trasformazione ADU -> Sigma (Z-Score)
        var sigmaProfile = _radiometry.ComputeSigmaProfile(null, currentProfile.BlackAdu, currentProfile.WhiteAdu, currentMetrics);

        // Calcolo metriche immagine successiva per il mapping inverso
        var nextMetrics = _radiometry.ComputeStatistics(nextMat);

        // Trasformazione Sigma -> ADU (sulla nuova immagine)
        return _radiometry.ComputeAbsoluteFromSigma(nextMat, sigmaProfile, nextMetrics);
    }
}