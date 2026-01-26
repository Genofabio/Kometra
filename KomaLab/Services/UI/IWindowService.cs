using System.Collections.Generic;
using System.Threading.Tasks;
using KomaLab.Models.Fits;
using KomaLab.Models.Fits.Structure;
using KomaLab.Models.Processing.Enhancement;
using KomaLab.Models.Visualization;
using KomaLab.ViewModels.Nodes;

namespace KomaLab.Services.UI;

public interface IWindowService
{
    void RegisterMainWindow(Avalonia.Controls.Window window);

    // Allineamento: riceve i percorsi e la modalità iniziale
    Task<List<string>?> ShowAlignmentWindowAsync(List<FitsFileReference> sourceFiles, VisualizationMode initialMode = VisualizationMode.Linear);

    // Header Editor: riceve Files e Navigator (Decoupled!)
    Task<FitsHeader?> ShowHeaderEditorAsync(IReadOnlyList<FitsFileReference> files, IImageNavigator navigator);

    // Plate Solving: riceve il nodo (perché deve scrivere i risultati WCS nel Renderer attivo)
    Task ShowPlateSolvingWindowAsync(ImageNodeViewModel node);

    // Posterizzazione: riceve i percorsi e la modalità
    Task<List<string>?> ShowPosterizationWindowAsync(List<FitsFileReference> sourceFiles, VisualizationMode initialMode);
    
    // Aggiungi questo metodo all'interfaccia
    Task<List<string>?> ShowImportWindowAsync();
    
    Task<(List<string> Paths, ImageEnhancementMode Mode)?> ShowRadialEnhancementWindowAsync(List<FitsFileReference> files, VisualizationMode mode);

    Task<(List<string> Paths, ImageEnhancementMode Mode)?> ShowStructureExtractionWindowAsync(
        List<FitsFileReference> sourceFiles,
        VisualizationMode initialMode);

    Task<(List<string> Paths, ImageEnhancementMode Mode)?> ShowLocalContrastWindowAsync(
        List<FitsFileReference> sourceFiles,
        VisualizationMode initialMode);
}