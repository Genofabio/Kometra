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