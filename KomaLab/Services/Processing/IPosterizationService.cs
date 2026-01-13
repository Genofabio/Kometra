using System.Collections.Generic;
using System.Threading.Tasks;
using KomaLab.Models.Visualization;
using OpenCvSharp; // Necessario per Mat

namespace KomaLab.Services.Processing;

public interface IPosterizationService
{
    // Calcolo Anteprima in Memoria (usato dal ViewModel per il Double Buffering)
    void ComputePosterizationOnMat(
        Mat src, 
        Mat dst, 
        int levels, 
        VisualizationMode mode, 
        double blackPoint, 
        double whitePoint
    );

    // Singolo file con punti fissi (Salvataggio su disco)
    Task<string> PosterizeAndSaveAsync(
        string inputPath,
        string outputFolder,
        int levels,
        VisualizationMode mode,
        double blackPoint,
        double whitePoint
    );

    // Batch di file con offset auto-adattivi (Elaborazione serie)
    Task<List<string>> PosterizeBatchWithOffsetsAsync(
        List<string> inputPaths,
        string outputFolder,
        int levels,
        VisualizationMode mode,
        double blackOffset,
        double whiteOffset
    );
}