using System.Collections.Generic;
using System.Threading.Tasks;
using KomaLab.Models.Fits;
using KomaLab.Models.Fits.Structure;
using KomaLab.Models.Processing.Enhancement;
using KomaLab.Models.Visualization;
using KomaLab.ViewModels.Nodes;

namespace KomaLab.Services.UI;

/// <summary>
/// Interfaccia per la gestione delle finestre modali e dei tool di elaborazione di KomaLab.
/// </summary>
public interface IWindowService
{
    void RegisterMainWindow(Avalonia.Controls.Window window);

    // --- Elaborazione Geometrica e Correzione ---
    
    Task<List<string>?> ShowAlignmentWindowAsync(List<FitsFileReference> sourceFiles, VisualizationMode initialMode = VisualizationMode.Linear);

    Task<FitsHeader?> ShowHeaderEditorAsync(IReadOnlyList<FitsFileReference> files, IImageNavigator navigator);

    Task ShowPlateSolvingWindowAsync(ImageNodeViewModel node);

    // --- Importazione e Calibrazione ---
    
    Task<List<string>?> ShowImportWindowAsync();

    // --- Image Enhancement (Filtri e Analisi) ---
    
    Task<List<string>?> ShowPosterizationWindowAsync(List<FitsFileReference> sourceFiles, VisualizationMode initialMode);

    Task<(List<string> Paths, ImageEnhancementMode Mode)?> ShowRadialEnhancementWindowAsync(List<FitsFileReference> files, VisualizationMode mode);

    Task<(List<string> Paths, ImageEnhancementMode Mode)?> ShowStructureExtractionWindowAsync(List<FitsFileReference> sourceFiles, VisualizationMode initialMode);

    Task<(List<string> Paths, ImageEnhancementMode Mode)?> ShowLocalContrastWindowAsync(List<FitsFileReference> sourceFiles, VisualizationMode initialMode);
    
    // --- Esportazione (Nuovo Signature) ---

    /// <summary>
    /// Mostra la finestra di configurazione per l'esportazione video.
    /// </summary>
    /// <param name="node">Il nodo immagine da cui estrarre risoluzione e frame totali.</param>
    /// <param name="currentMode">La modalità di visualizzazione corrente (Linear/Log/etc).</param>
    /// <returns>Le impostazioni di esportazione confermate o null se annullato.</returns>
    Task<VideoExportSettings?> ShowVideoExportDialogAsync(ImageNodeViewModel node, VisualizationMode currentMode);
}