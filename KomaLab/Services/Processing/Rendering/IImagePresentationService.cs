using KomaLab.Models.Visualization;
using OpenCvSharp;

namespace KomaLab.Services.Processing.Rendering;

public interface IImagePresentationService
{
    // --- ESECUZIONE ---
    void RenderTo8Bit(Mat src, Mat dst, double blackPoint, double whitePoint, VisualizationMode mode);

    // --- REQUISITI DI PRESENTAZIONE ---
    (double Mean, double StdDev) GetPresentationRequirements(Mat source);

    // --- SOGLIE E TRANSIZIONE ---
    AbsoluteContrastProfile GetInitialProfile(Mat source);
    SigmaContrastProfile GetRelativeProfile(AbsoluteContrastProfile absolute, (double Mean, double StdDev) stats);
    AbsoluteContrastProfile GetAbsoluteProfile(SigmaContrastProfile relative, (double Mean, double StdDev) stats);
}