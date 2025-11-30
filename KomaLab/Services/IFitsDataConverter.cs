using KomaLab.Models;
using OpenCvSharp;

namespace KomaLab.Services;

public interface IFitsDataConverter
{
    /// <summary>
    /// Converte i dati grezzi FITS (Array jagged di vari tipi) 
    /// in una Matrice OpenCV CV_64FC1 (Double).
    /// Gestisce automaticamente BZERO e BSCALE.
    /// </summary>
    Mat RawToMat(FitsImageData fitsData);

    /// <summary>
    /// Converte una Matrice OpenCV (Double) in una struttura FitsImageData 
    /// pronta per il salvataggio o la cache.
    /// Pulisce l'header dai vecchi BZERO/BSCALE.
    /// </summary>
    FitsImageData MatToFitsData(Mat mat, FitsImageData originalTemplate);

    /// <summary>
    /// Calcola soglie di visualizzazione (Black/White) campionando i dati raw
    /// SENZA allocare una Matrice OpenCV (molto leggero).
    /// </summary>
    (double Black, double White) CalculateDisplayThresholds(FitsImageData data);
}