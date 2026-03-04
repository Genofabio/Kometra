using OpenCvSharp;

namespace Kometra.Services.Processing.Engines;

public interface IInpaintingEngine
{
    Mat InpaintStars(Mat image, Mat starMask, Mat cometMask = null);
}