using System.Threading.Tasks; // Non strettamente necessario qui, ma buona pratica nei using
using KomaLab.Models.Fits;
using OpenCvSharp;

namespace KomaLab.Services.Data;

// ---------------------------------------------------------------------------
// FILE: IFitsImageDataConverter.cs
// DESCRIZIONE:
// Convertitore puro di dati binari (Pixel Data).
// Si occupa esclusivamente della trasformazione ad alte prestazioni tra:
// - Array C# gestiti (formato FITS raw)
// - Matrici OpenCV non gestite (formato elaborazione)
// NON gestisce i metadati o la logica di header (delegata a IFitsMetadataService).
// ---------------------------------------------------------------------------

public interface IFitsImageDataConverter
{
    /// <summary>
    /// Converte l'intera immagine FITS in una Matrice OpenCV (Double Precision).
    /// </summary>
    Mat RawToMat(FitsImageData fitsData);
    
    /// <summary>
    /// Converte solo una porzione rettangolare (striscia orizzontale).
    /// Ottimizzato per l'elaborazione a chunk (es. Stacking) per risparmiare RAM.
    /// </summary>
    Mat RawToMatRect(FitsImageData fitsData, int yStart, int rowsToRead);

    /// <summary>
    /// Converte una Matrice OpenCV in dati FITS.
    /// </summary>
    /// <param name="mat">Matrice sorgente.</param>
    /// <param name="targetDepth">
    /// Formato di destinazione. 
    /// Default: Double (-64) per massima precisione scientifica.
    /// </param>
    FitsImageData MatToFitsData(Mat mat, FitsBitDepth targetDepth = FitsBitDepth.Double);
}