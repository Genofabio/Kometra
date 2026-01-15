using System.Collections.Generic;
using System.Threading.Tasks;
using KomaLab.Models.Fits; // Per FitsCollection
using KomaLab.ViewModels.Nodes;

namespace KomaLab.Services.Factories;

// ---------------------------------------------------------------------------
// FILE: INodeViewModelFactory.cs
// DESCRIZIONE:
// Factory pattern per la creazione asincrona dei nodi.
// VERSIONE: Aggiornata per FitsCollection e caricamento Header-Only.
// ---------------------------------------------------------------------------

public interface INodeViewModelFactory
{
    /// <summary>
    /// Crea un nodo immagine singola da un percorso file.
    /// Legge solo l'header per determinare le dimensioni iniziali.
    /// </summary>
    Task<SingleImageNodeViewModel> CreateSingleImageNodeAsync(string path, double x, double y, bool centerOnPosition = false);
    
    /// <summary>
    /// Crea un nodo immagine singola da una collezione esistente (es. output di un altro nodo).
    /// </summary>
    Task<SingleImageNodeViewModel> CreateNodeFromCollectionAsync(FitsCollection collection, string title, double x, double y);
    
    /// <summary>
    /// Crea un nodo multi-immagine (stacking/video).
    /// </summary>
    Task<MultipleImagesNodeViewModel> CreateMultipleImagesNodeAsync(List<string> paths, double x, double y, bool centerOnPosition = false);
}