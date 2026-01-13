using KomaLab.Models.Fits;
using KomaLab.ViewModels.Visualization;

namespace KomaLab.Services.Factories;

public interface IFitsRendererFactory
{
    /// <summary>
    /// Crea una nuova istanza di FitsRenderer con le dipendenze già iniettate.
    /// </summary>
    FitsRenderer Create(FitsImageData data);
}