using System.Collections.Generic;
using System.Threading.Tasks;
using KomaLab.Models.Export;
using KomaLab.Models.Fits;
using KomaLab.Models.Fits.Structure;
using KomaLab.Models.Processing.Enhancement;
using KomaLab.Models.Visualization;
using KomaLab.ViewModels.Nodes;

namespace KomaLab.Services.UI;

public interface IWindowService
{
    void RegisterMainWindow(Avalonia.Controls.Window window);
    
    // Import
    Task<(List<string> Paths, bool SeparateNodes)?> ShowImportWindowAsync();
    
    // Export
    Task<VideoExportSettings?> ShowVideoExportDialogAsync(ImageNodeViewModel node, VisualizationMode currentMode);
    
    // Batch Export (La firma rimane invariata qui)
    Task ShowExportWindowAsync(IEnumerable<string> filePaths);

    // Tools
    Task<List<string>?> ShowAlignmentWindowAsync(List<FitsFileReference> sourceFiles, VisualizationMode initialMode = VisualizationMode.Linear);
    Task<FitsHeader?> ShowHeaderEditorAsync(IReadOnlyList<FitsFileReference> files, IImageNavigator navigator);
    Task ShowPlateSolvingWindowAsync(ImageNodeViewModel node);
    Task<List<string>?> ShowStarMaskingWindowAsync(List<FitsFileReference> sourceFiles);
    Task<List<string>?> ShowPosterizationWindowAsync(List<FitsFileReference> sourceFiles, VisualizationMode initialMode);
    Task<List<string>?> ShowCropToolWindowAsync(List<FitsFileReference> sourceFiles, VisualizationMode initialMode);
    
    // Enhancement
    Task<(List<string> Paths, ImageEnhancementMode Mode)?> ShowRadialEnhancementWindowAsync(List<FitsFileReference> sourceFiles, VisualizationMode initialMode);
    Task<(List<string> Paths, ImageEnhancementMode Mode)?> ShowStructureExtractionWindowAsync(List<FitsFileReference> sourceFiles, VisualizationMode initialMode);
    Task<(List<string> Paths, ImageEnhancementMode Mode)?> ShowLocalContrastWindowAsync(List<FitsFileReference> sourceFiles, VisualizationMode initialMode);
}