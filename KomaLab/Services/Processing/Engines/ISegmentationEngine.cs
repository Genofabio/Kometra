using KomaLab.Models.Processing.Masking;
using OpenCvSharp;

namespace KomaLab.Services.Processing.Engines;

public interface ISegmentationEngine
{
    Mat ComputeCometMask(Mat image, double backgroundLevel, double noiseStdDev, MaskingParameters p);
    Mat ComputeStarMask(Mat image, Mat cometMask, double backgroundLevel, double noiseStdDev, MaskingParameters p);
}