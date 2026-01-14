using System;

namespace KomaLab.Models.Fits;

// ---------------------------------------------------------------------------
// FILE: FitsImageData.cs
// DESCRIZIONE:
// DTO che racchiude i dati grezzi dell'immagine (matrice pixel) e i metadati.
// ---------------------------------------------------------------------------

public class FitsImageData
{
    public required Array RawData { get; set; } 

    public required FitsHeader FitsHeader { get; set; }

    public int Width { get; set; }
    public int Height { get; set; }

    public Type PixelType => RawData.GetType().GetElementType() ?? typeof(object);

    public T[,] GetData<T>()
    {
        if (RawData is T[,] data)
        {
            return data;
        }
        throw new InvalidCastException($"Impossibile convertire i dati FITS da {PixelType.Name}[,] a {typeof(T).Name}[,]");
    }

    /// <summary>
    /// Crea una COPIA PROFONDA (Deep Copy) dell'intera immagine.
    /// Duplica sia i pixel in memoria che l'header.
    /// Utile per creare snapshot o copie di backup prima di elaborazioni distruttive.
    /// </summary>
    public FitsImageData Clone()
    {
        return new FitsImageData
        {
            // Array.Clone() su array di primitive (int, double) crea una nuova allocazione
            // e copia i valori. È una copia sicura dei pixel.
            RawData = (Array)this.RawData.Clone(),
            
            // Usiamo il metodo Clone() che abbiamo appena aggiunto a FitsHeader
            FitsHeader = this.FitsHeader.Clone(),
            
            // Tipi valore, copia diretta
            Width = this.Width,
            Height = this.Height
        };
    }
}