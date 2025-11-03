using System.IO;
using System.Threading.Tasks;
using KomaLab.Models;
using KomaLab.ViewModels;

namespace KomaLab.Services;

/// <summary>
/// Implementazione concreta di INodeViewModelFactory.
/// </summary>
public class NodeViewModelFactory : INodeViewModelFactory
{
    // Costante per il calcolo del centraggio
    private const double ESTIMATED_NON_IMAGE_HEIGHT = 60.0;
    
    // Dipendenza dal servizio che processa le immagini
    private readonly IFitsService _fitsService;

    /// <summary>
    /// Costruttore che riceve le dipendenze necessarie.
    /// </summary>
    public NodeViewModelFactory(IFitsService fitsService)
    {
        _fitsService = fitsService;
    }

    /// <summary>
    /// Crea, carica e posiziona un nuovo NodeViewModel.
    /// </summary>
    public async Task<NodeViewModel> CreateNodeAsync(BoardViewModel parent, string imagePath, double x, double y, bool centerOnPosition = false)
    {
        // 1. Crea il Modello
        var newNodeModel = new NodeModel
        {
            ImagePath = imagePath,
            Title = Path.GetFileName(imagePath),
            X = x,
            Y = y
        };
    
        // 2. Crea il ViewModel, iniettando le sue dipendenze (il FitsService)
        var newNodeViewModel = new NodeViewModel(parent, newNodeModel, _fitsService);

        // 3. ATTENDE il caricamento (fondamentale per avere ImageSize)
        await newNodeViewModel.LoadDataAsync();

        // 4. Calcola la posizione finale se richiesto
        if (centerOnPosition && newNodeViewModel.ImageSize.Width > 0)
        {
            double nodeImageWidth = newNodeViewModel.ImageSize.Width;
            double nodeImageHeight = newNodeViewModel.ImageSize.Height;
            
            // Calcola l'offset dell'interfaccia utente (titolo, ecc.) in coordinate "mondo"
            double uiHeightWorld = ESTIMATED_NON_IMAGE_HEIGHT / parent.Scale;
            double totalNodeHeight = nodeImageHeight + uiHeightWorld;
            
            // Applica l'offset per centrare il nodo
            newNodeViewModel.X = x - (nodeImageWidth / 2);
            newNodeViewModel.Y = y - (totalNodeHeight / 2);
        }
        
        // 5. Restituisce il VM pronto
        return newNodeViewModel;
    }
}