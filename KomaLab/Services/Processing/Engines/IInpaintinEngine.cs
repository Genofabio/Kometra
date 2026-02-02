using OpenCvSharp;

namespace KomaLab.Services.Processing.Engines;

public interface IInpaintingEngine
{
    Mat InpaintStars(Mat image, Mat mask);
}