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
    // 1. RENDERING (Pipeline di Stretching & Display)
    // =======================================================================

    public void RenderTo8Bit(Mat src, Mat dst, double blackPoint, double whitePoint, VisualizationMode mode)
    {
        if (src == null || src.Empty()) return;

        // Calcolo del range dinamico
        double range = whitePoint - blackPoint;
        if (Math.Abs(range) < 1e-9) range = 1.0;

        // alpha = moltiplicatore, beta = offset
        // Formula: dst = src * alpha + beta
        double alpha = 1.0 / range;
        double beta = -blackPoint * alpha;

        using Mat temp32F = new Mat();
        
        // Portiamo i dati nel range [0.0, 1.0]. 
        // ConvertTo gestisce internamente sia sorgenti 32F che 64F.
        src.ConvertTo(temp32F, MatType.CV_32FC1, alpha, beta);

        // Clipping: limitiamo i valori per evitare artefatti fuori dal range 0-1
        Cv2.Max(temp32F, 0.0, temp32F);
        Cv2.Min(temp32F, 1.0, temp32F);

        // Applicazione funzione di trasferimento (non lineare)
        ApplyTransferFunction(temp32F, mode);

        // Trasformazione finale in 8-bit Gray per Avalonia
        temp32F.ConvertTo(dst, MatType.CV_8UC1, 255.0);
    }

    private void ApplyTransferFunction(Mat mat, VisualizationMode mode)
    {
        switch (mode)
        {
            case VisualizationMode.SquareRoot: 
                Cv2.Sqrt(mat, mat); 
                break;
                
            case VisualizationMode.Logarithmic:
                // Log(1 + x) per gestire i valori vicino allo zero
                Cv2.Add(mat, 1.0, mat);
                Cv2.Log(mat, mat);
                // Normalizzazione logaritmica (log2(1+1) = 1)
                Cv2.Multiply(mat, 1.442695, mat); 
                break;
        }
    }

    // =======================================================================
    // 2. LOGICA DI PRESENTAZIONE (Statistiche & Profili)
    // =======================================================================

    public (double Mean, double StdDev) GetPresentationRequirements(Mat source)
    {
        // Delega l'analisi dei pixel al motore radiometrico
        return _radiometry.ComputeStatistics(source);
    }

    public AbsoluteContrastProfile GetInitialProfile(Mat source)
    {
        // Ottiene l'AutoStretch iniziale basato sui quantili
        return _radiometry.CalculateAutoStretchProfile(source);
    }

    /// <summary>
    /// Converte soglie ADU assolute in un profilo relativo basato sui Sigma.
    /// Operazione O(1) - Non tocca i pixel.
    /// </summary>
    public SigmaContrastProfile GetRelativeProfile(AbsoluteContrastProfile absolute, (double Mean, double StdDev) stats)
    {
        return _radiometry.ComputeSigmaProfile(absolute.BlackAdu, absolute.WhiteAdu, stats);
    }

    /// <summary>
    /// Traduce un profilo relativo (Sigma) in soglie ADU per l'immagine specifica.
    /// Operazione O(1) - Non tocca i pixel.
    /// </summary>
    public AbsoluteContrastProfile GetAbsoluteProfile(SigmaContrastProfile relative, (double Mean, double StdDev) stats)
    {
        return _radiometry.ComputeAbsoluteFromSigma(relative, stats);
    }
}