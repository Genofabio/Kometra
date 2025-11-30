using System;
using nom.tam.fits;

namespace KomaLab.Models;

public class FitsImageData
{
    /// <summary>
    /// L'array multidimensionale contenente i dati dei pixel.
    /// Solitamente è di tipo short[,], int[,] o float[,] a seconda del BITPIX.
    /// </summary>
    public required object RawData { get; set; } 

    /// <summary>
    /// L'header originale del file FITS (metadati).
    /// </summary>
    public required Header FitsHeader { get; set; }

    /// <summary>
    /// Larghezza dell'immagine in pixel.
    /// </summary>
    public int Width { get; set; }

    /// <summary>
    /// Altezza dell'immagine in pixel.
    /// </summary>
    public int Height { get; set; }

    /// <summary>
    /// Helper property (sola lettura) per identificare rapidamente il tipo di dato contenuto.
    /// Utile per switchare la logica di elaborazione (es. 16bit vs 32bit float).
    /// </summary>
    public Type PixelType => RawData?.GetType() ?? typeof(object);
}