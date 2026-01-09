using System.Threading.Tasks;
using KomaLab.Models;
using KomaLab.Models.Visualization;

namespace KomaLab.Services.Imaging;

public interface IPosterizationService
{
    Task<string> PosterizeAndSaveAsync(
        string inputPath,
        string outputFolder,
        int levels,
        VisualizationMode mode,
        double blackPoint,
        double whitePoint
    );
}