using System;

namespace Kometra.Models.Fits.Structure;

/// <summary>
/// Rappresenta una singola Header Data Unit (HDU) all'interno di un file FITS.
/// Può essere il Primary HDU o una Image Extension.
/// </summary>
public record FitsHdu(
    FitsHeader Header, 
    Array PixelData,
    bool IsEmpty // Utile per sapere se è solo un contenitore di metadati (es. Primary HDU vuoto)
);