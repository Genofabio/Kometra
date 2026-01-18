using System;

namespace KomaLab.Models.Fits.Structure;

/// <summary>
/// Pacchetto atomico contenente i dati grezzi di un file FITS.
/// </summary>
public record FitsDataPackage(
    string FilePath,
    FitsHeader Header,
    Array PixelData
);