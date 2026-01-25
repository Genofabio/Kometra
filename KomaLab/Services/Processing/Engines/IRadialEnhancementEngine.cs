using OpenCvSharp;

namespace KomaLab.Services.Processing.Engines;

public interface IRadialEnhancementEngine
{
    /// <summary>
    /// Applica il miglioramento 1/rho moltiplicando ogni pixel per la sua distanza media dal centro.
    /// </summary>
    void ApplyInverseRho(Mat src, Mat dst, int subsampling = 10);

    /// <summary>
    /// Converte l'immagine in coordinate polari (unwarping).
    /// </summary>
    Mat ToPolar(Mat src, int nRad, int nTheta, int subsampling = 10);

    /// <summary>
    /// Ricostruisce l'immagine dalle coordinate polari a cartesiane (rewarping).
    /// </summary>
    void FromPolar(Mat polar, Mat dst, int width, int height, int subsampling = 10);

    /// <summary>
    /// Calcola e applica il modello della media azimutale con rigetto sigma.
    /// </summary>
    void ApplyAzimuthalAverage(Mat polar, double rejSig);

    /// <summary>
    /// Calcola e applica il modello della mediana azimutale.
    /// </summary>
    void ApplyAzimuthalMedian(Mat polar);

    /// <summary>
    /// Applica la rinormalizzazione locale del contrasto basata sullo Z-score statistico.
    /// </summary>
    void ApplyAzimuthalRenormalization(Mat polar, double rejSig, double nSig);
}