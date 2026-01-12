using System.Collections.Generic;
using System.Threading.Tasks;
using KomaLab.Models.Visualization;

namespace KomaLab.Services.Imaging;

public interface IPosterizationService
{
    // Singolo file con punti fissi (usato per l'immagine corrente)
    Task<string> PosterizeAndSaveAsync(
        string inputPath,
        string outputFolder,
        int levels,
        VisualizationMode mode,
        double blackPoint,
        double whitePoint
    );

    // Batch di file con offset auto-adattivi (usato per la serie)
    Task<List<string>> PosterizeBatchWithOffsetsAsync(
        List<string> inputPaths,
        string outputFolder,
        int levels,
        VisualizationMode mode,
        double blackOffset,
        double whiteOffset
    );
}