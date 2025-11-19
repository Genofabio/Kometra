using System;
using Avalonia;
using KomaLab.Models;
using nom.tam.fits;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace KomaLab.Services;

public interface IFitsService
{
    /// <summary>
    /// Carica i DATI COMPLETI (pesante) di un file FITS.
    /// Restituisce null se il caricamento fallisce.
    /// </summary>
    Task<FitsImageData?> LoadFitsFromFileAsync(string assetPath);

    /// <summary>
    /// Legge SOLO L'HEADER (leggero) di un file FITS per restituirne la dimensione.
    /// </summary>
    Task<Size> GetFitsImageSizeAsync(string path);

    /// <summary>
    /// Normalizza una Matrice OpenCV pre-caricata in un array di byte Gray8
    /// usando le soglie specificate.
    /// </summary>
    void NormalizeData(
        OpenCvSharp.Mat sourceMat, // NUOVA SORGENTE: La Matrice cached
        int width, 
        int height,
        double blackPoint, 
        double whitePoint,
        IntPtr destinationBuffer, 
        long stride);

    public Task SaveFitsFileAsync(FitsImageData data, string destinationPath);

}