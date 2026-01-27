using System;
using System.Collections.Generic;
using System.Linq;

namespace KomaLab.Models.Fits.Structure;

/// <summary>
/// Pacchetto contenente TUTTE le estensioni presenti nel file FITS.
/// </summary>
public record FitsDataPackage(
    string FilePath,
    List<FitsHdu> Hdus
)
{
    // Helpers per retrocompatibilità o accesso rapido
    public FitsHdu PrimaryHdu => Hdus.FirstOrDefault();
    
    // Restituisce la prima immagine valida (non vuota) trovata nel file.
    // Utile per visualizzatori che supportano una sola immagine alla volta.
    public FitsHdu? FirstImageHdu => Hdus.FirstOrDefault(h => !h.IsEmpty);
}