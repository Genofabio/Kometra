using Avalonia;
using nom.tam.fits;
using System.Threading.Tasks;
using KomaLab.Models;

namespace KomaLab.Services;

public interface IFitsService
{
    /// <summary>
    /// Carica un file FITS da un percorso asset, lo parsa e calcola le soglie iniziali.
    /// </summary>
    /// <param name="assetPath">Il percorso URI dell'asset (es. "avares://...")</param>
    /// <returns>Un DTO FitsImageData con tutti i dati estratti.</returns>
    Task<FitsImageData> LoadFitsFromFileAsync(string assetPath);

    /// <summary>
    /// Normalizza i dati FITS grezzi in un array di byte (Gray8) 
    /// usando le soglie specificate.
    /// </summary>
    /// <param name="rawData">L'array grezzo (es. float[][])</param>
    /// <param name="header">L'header FITS (per BITPIX)</param>
    /// <param name="width">Larghezza immagine</param>
    /// <param name="height">Altezza immagine</param>
    /// <param name="blackPoint">Soglia del nero</param>
    /// <param name="whitePoint">Soglia del bianco</param>
    /// <returns>Un array di byte pronto per un Bitmap Gray8.</returns>
    byte[] NormalizeData(object rawData, Header header, int width, int height, double blackPoint, double whitePoint);

    /// <summary>
    /// Calcola le soglie del punto di nero e del punto di bianco analizzando i dati grezzi dell'immagine.
    /// Questo metodo è tipicamente usato per trovare i valori iniziali ottimali tramite un 'clipping' dei percentili.
    /// </summary>
    /// <param name="rawData">I dati FITS grezzi (es. float[][] o short[][]).</param>
    /// <param name="header">L'header FITS, necessario per determinare il tipo di dati (BITPIX).</param>
    /// <returns>Una tupla contenente i valori calcolati di BlackPoint e WhitePoint.</returns>
    (double BlackPoint, double WhitePoint) CalculateClippedThresholds(object rawData, Header header);
}