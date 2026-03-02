using OpenCvSharp;

namespace Kometra.Services.Processing.Engines;

public interface ICalibrationEngine
{
    /// <summary>
    /// Applica la calibrazione completa a un frame Light.
    /// Formula: (Light - Dark) / ((Flat - Bias) / Mean)
    /// </summary>
    Mat ApplyCalibration(Mat light, Mat? masterDark, Mat? masterFlat, Mat? masterBias);
}