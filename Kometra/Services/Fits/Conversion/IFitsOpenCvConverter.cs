using System;
using Kometra.Models.Fits;
using Kometra.Models.Fits.Structure;
using OpenCvSharp;

namespace Kometra.Services.Fits.Conversion;

/// <summary>
/// Servizio per la conversione bidirezionale tra dati FITS raw (Array C#) 
/// e matrici di calcolo scientifico OpenCV (Mat).
/// </summary>
public interface IFitsOpenCvConverter
{
    /// <summary>
    /// Converte un array raw FITS in una matrice OpenCV Floating Point.
    /// </summary>
    /// <param name="rawPixels">Array multidimensionale sorgente.</param>
    /// <param name="bScale">Fattore di scala (BSCALE).</param>
    /// <param name="bZero">Offset (BZERO).</param>
    /// <param name="targetDepth">
    /// Se null, applica la promozione intelligente: 8/16/-32 bit -> 32-bit Float, 32/-64 bit -> 64-bit Float.
    /// Se specificato (es. Float o Double), forza la precisione della matrice risultante.
    /// </param>
    /// <returns>Una matrice OpenCV (CV_32FC1 o CV_64FC1) con i valori scalati.</returns>
    Mat RawToMat(Array rawPixels, double bScale = 1.0, double bZero = 0.0, FitsBitDepth? targetDepth = null);
    
    /// <summary>
    /// Converte una porzione dell'array raw FITS (strip) con la profondità di bit specificata.
    /// </summary>
    /// <param name="yStart">Riga di partenza nell'array sorgente.</param>
    /// <param name="rowsToRead">Numero di righe da processare.</param>
    /// <param name="targetDepth">Profondità esplicita (es. FitsBitDepth.Float o FitsBitDepth.Double).</param>
    //Mat RawToMatRect(Array rawPixels, int yStart, int rowsToRead, double bScale = 1.0, double bZero = 0.0, FitsBitDepth targetDepth = FitsBitDepth.Double);

    /// <summary>
    /// Converte una matrice OpenCV in un Array C# multidimensionale compatibile con lo standard FITS.
    /// </summary>
    /// <param name="mat">Matrice sorgente (Floating Point).</param>
    /// <param name="targetDepth">Profondità di bit target per l'array (BITPIX).</param>
    /// <returns>Array multidimensionale (es. short[,], int[,], double[,]).</returns>
    Array MatToRaw(Mat mat, FitsBitDepth targetDepth = FitsBitDepth.Double);
}