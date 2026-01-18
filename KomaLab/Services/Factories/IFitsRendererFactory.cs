using System;
using KomaLab.Models.Fits;
using KomaLab.Models.Fits.Structure;
using KomaLab.ViewModels.Visualization;

namespace KomaLab.Services.Factories;

public interface IFitsRendererFactory
{
    /// <summary>
    /// Crea una nuova istanza di FitsRenderer iniettando automaticamente i servizi necessari.
    /// Richiede i dati grezzi (Pixel) e l'Header (per BSCALE/BZERO).
    /// </summary>
    FitsRenderer Create(Array pixelData, FitsHeader header);
}