using System.Collections.Generic;
using System.Threading.Tasks;
using KomaLab.Models;
using KomaLab.ViewModels;

namespace KomaLab.Services.Factories;

public interface INodeViewModelFactory
{
    Task<SingleImageNodeViewModel> CreateSingleImageNodeAsync(string path, double x, double y, bool centerOnPosition = false);
    
    Task<SingleImageNodeViewModel> CreateSingleImageNodeFromDataAsync(FitsImageData data, string title, double x, double y);
    
    Task<MultipleImagesNodeViewModel> CreateMultipleImagesNodeAsync(List<string> paths, double x, double y, bool centerOnPosition = false);
}