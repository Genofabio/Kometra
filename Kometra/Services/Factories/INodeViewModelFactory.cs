using System.Collections.Generic;
using System.Threading.Tasks;
using Kometra.ViewModels.Nodes;

namespace Kometra.Services.Factories;

public interface INodeViewModelFactory
{
    // Crea un nodo da un singolo percorso file
    Task<SingleImageNodeViewModel> CreateSingleImageNodeAsync(string path, double x, double y);
    
    // Crea un nodo da un elenco di percorsi (sequenza)
    Task<MultipleImagesNodeViewModel> CreateMultipleImagesNodeAsync(List<string> paths, double x, double y);
}