using System.Collections.Generic;
using System.Threading.Tasks;
using Kometra.Models.Export;
using Kometra.Models.Fits;
using Kometra.Models.Fits.Structure;
using Kometra.Models.Processing.Enhancement;
using Kometra.Models.Visualization;
using Kometra.ViewModels.Nodes;

namespace Kometra.Services.UI;

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
    Task ShowSettingsWindowAsync();
    
    // Enhancement
    Task<(List<string> Paths, ImageEnhancementMode Mode)?> ShowRadialEnhancementWindowAsync(List<FitsFileReference> sourceFiles, VisualizationMode initialMode);
    Task<(List<string> Paths, ImageEnhancementMode Mode)?> ShowStructureExtractionWindowAsync(List<FitsFileReference> sourceFiles, VisualizationMode initialMode);
    Task<(List<string> Paths, ImageEnhancementMode Mode)?> ShowLocalContrastWindowAsync(List<FitsFileReference> sourceFiles, VisualizationMode initialMode);
}