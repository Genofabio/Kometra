using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KomaLab.Models.Fits;
using KomaLab.Models.Visualization;

namespace KomaLab.Services.Processing.Coordinators;

public interface IVideoExportCoordinator
{
    Task ExportVideoAsync(
        IEnumerable<FitsFileReference> sourceFiles, // Cambiato da string a FitsFileReference
        string outputFilePath, 
        double fps,
        AbsoluteContrastProfile initialProfile,
        VisualizationMode mode,
        bool adaptiveStretch = true,
        CancellationToken token = default);
}