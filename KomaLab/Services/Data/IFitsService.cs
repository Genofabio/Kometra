using System;
using System.Threading.Tasks;
using KomaLab.Models;
using OpenCvSharp;

namespace KomaLab.Services.Data;

public interface IFitsService
{
    /// <summary>
    /// Carica i DATI COMPLETI di un file FITS.
    /// </summary>
    Task<FitsImageData?> LoadFitsFromFileAsync(string assetPath);

    /// <summary>
    /// Legge solo la dimensione dell'immagine (Header).
    /// Restituisce (Width, Height).
    /// </summary>
    Task<(int Width, int Height)> GetFitsImageSizeAsync(string path);

    /// <summary>
    /// Converte una Matrice OpenCV (contenente dati scientifici) 
    /// in un buffer di memoria per visualizzazione (Bitmap 8-bit).
    /// </summary>
    void NormalizeData(
        Mat sourceMat, 
        int width, 
        int height,
        double blackPoint, 
        double whitePoint,
        IntPtr destinationBuffer, 
        long stride);

    Task SaveFitsFileAsync(FitsImageData data, string destinationPath);
}