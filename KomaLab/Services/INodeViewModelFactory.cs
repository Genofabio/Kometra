using System.Collections.Generic;
using System.Threading.Tasks;
using KomaLab.ViewModels;

namespace KomaLab.Services;

/// <summary>
/// Interfaccia per una factory responsabile della creazione 
/// e inizializzazione di istanze di SingleImageNodeViewModel.
/// </summary>
public interface INodeViewModelFactory
{
    /// <summary>
    /// Crea un nuovo SingleImageNodeViewModel, carica i suoi dati e ne calcola la posizione.
    /// </summary>
    /// <param name="parent">Il BoardViewModel genitore.</param>
    /// <param name="imagePath">Il percorso dell'asset dell'immagine FITS.</param>
    /// <param name="x">La coordinata X nel mondo.</param>
    /// <param name="y">La coordinata Y nel mondo.</param>
    /// <param name="centerOnPosition">Se true, centra il nodo sulle coordinate (x, y) dopo il caricamento.</param>
    /// <returns>Un'istanza di SingleImageNodeViewModel pronta per essere aggiunta alla scena.</returns>
    Task<SingleImageNodeViewModel> CreateSingleImageNodeAsync(
        BoardViewModel parent, 
        string imagePath, 
        double x, double y, 
        bool centerOnPosition = false);
    
    Task<MultipleImagesNodeViewModel> CreateMultipleImagesNodeAsync(
        BoardViewModel parent,
        List<string> imagePaths,
        double x, double y);
}