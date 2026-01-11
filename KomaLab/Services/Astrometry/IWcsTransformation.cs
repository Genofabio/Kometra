using KomaLab.Models;
using KomaLab.Models.Primitives;

namespace KomaLab.Services.Astrometry;

// ---------------------------------------------------------------------------
// FILE: IWcsTransformation.cs
// DESCRIZIONE:
// Contratto per il motore di trasformazione coordinate.
// Permette ai ViewModel di convertire pixel <-> cielo senza conoscere 
// i dettagli matematici o dipendere dall'implementazione concreta.
// ---------------------------------------------------------------------------

public interface IWcsTransformation
{
    /// <summary>
    /// Converte coordinate Celesti (RA, Dec in gradi) in coordinate Pixel (X, Y).
    /// </summary>
    Point2D? WorldToPixel(double raDeg, double decDeg);

    /// <summary>
    /// Converte coordinate Pixel (X, Y) in coordinate Celesti (RA, Dec in gradi).
    /// </summary>
    (double Ra, double Dec)? PixelToWorld(double x, double y);
}