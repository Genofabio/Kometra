using System;
using nom.tam.fits;

namespace KomaLab.Models.Fits;

// ---------------------------------------------------------------------------
// FILE: FitsImageData.cs
// DESCRIZIONE:
// DTO che racchiude i dati grezzi dell'immagine (matrice pixel) e i metadati.
// Fornisce metodi sicuri ed espliciti per l'accesso ai dati tipizzati.
// ---------------------------------------------------------------------------

public class FitsImageData
{
    /// <summary>
    /// L'array multidimensionale contenente i dati dei pixel.
    /// Usiamo 'Array' (anziché object) per garantire che sia una struttura dati iterabile.
    /// </summary>
    public required Array RawData { get; set; } 

    /// <summary>
    /// L'header originale del file FITS (metadati).
    /// </summary>
    public required Header FitsHeader { get; set; }

    /// <summary>
    /// Larghezza dell'immagine in pixel (dimensione X).
    /// </summary>
    public int Width { get; set; }

    /// <summary>
    /// Altezza dell'immagine in pixel (dimensione Y).
    /// </summary>
    public int Height { get; set; }

    /// <summary>
    /// Helper property per identificare il tipo di elemento contenuto nell'array (es. Int16, Double).
    /// </summary>
    public Type PixelType => RawData.GetType().GetElementType() ?? typeof(object);

    /// <summary>
    /// Metodo ESPLICITO per ottenere i dati tipizzati.
    /// Lancia un'eccezione chiara se il tipo richiesto non corrisponde.
    /// </summary>
    /// <typeparam name="T">Il tipo atteso dei pixel (es. short, float)</typeparam>
    public T[,] GetData<T>()
    {
        if (RawData is T[,] data)
        {
            return data;
        }

        throw new InvalidCastException(
            $"Impossibile convertire i dati FITS da {PixelType.Name}[,] a {typeof(T).Name}[,]");
    }
}