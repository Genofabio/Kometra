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
    /// Normalizza i dati grezzi in un array di byte per la visualizzazione.
    /// </summary>
    byte[] NormalizeData(object rawData, Header header, int width, int height, double blackPoint, double whitePoint);

    /// <summary>
    /// Calcola le soglie iniziali (percentili) dai dati grezzi.
    /// </summary>
    (double BlackPoint, double WhitePoint) CalculateClippedThresholds(object rawData, Header header);
}