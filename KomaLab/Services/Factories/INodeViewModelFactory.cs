using System.Collections.Generic;
using System.Threading.Tasks;
using KomaLab.Models.Fits;
using KomaLab.ViewModels;

namespace KomaLab.Services.Factories;

// ---------------------------------------------------------------------------
// FILE: INodeViewModelFactory.cs
// DESCRIZIONE:
// Factory pattern per la creazione asincrona dei nodi (ViewModel).
// Astrae la complessità di istanziazione (iniezione dipendenze, caricamento dati iniziale)
// permettendo alla MainView di creare nodi senza conoscere i servizi sottostanti.
// ---------------------------------------------------------------------------

public interface INodeViewModelFactory
{
    /// <summary>
    /// Crea un nodo immagine singola caricando il file da disco.
    /// </summary>
    Task<SingleImageNodeViewModel> CreateSingleImageNodeAsync(string path, double x, double y, bool centerOnPosition = false);
    
    /// <summary>
    /// Crea un nodo immagine singola partendo da dati già in memoria (es. risultato di uno stack o elaborazione).
    /// </summary>
    Task<SingleImageNodeViewModel> CreateSingleImageNodeFromDataAsync(FitsImageData data, string title, double x, double y);
    
    /// <summary>
    /// Crea un nodo multi-immagine (stacking/video), scansionando preventivamente gli header.
    /// </summary>
    Task<MultipleImagesNodeViewModel> CreateMultipleImagesNodeAsync(List<string> paths, double x, double y, bool centerOnPosition = false);
}