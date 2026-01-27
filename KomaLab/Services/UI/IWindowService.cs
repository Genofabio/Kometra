using System.Collections.Generic;
using System.Threading.Tasks;
using KomaLab.Models.Fits;
using KomaLab.Models.Fits.Structure;
using KomaLab.Models.Processing.Enhancement;
using KomaLab.Models.Visualization;
using KomaLab.ViewModels.Nodes;

namespace KomaLab.Services.UI;

/// <summary>
/// Interfaccia per l'orchestrazione delle finestre modali e dei tool di elaborazione di KomaLab.
/// Gestisce il ciclo di vita dei ViewModel e la restituzione dei risultati delle operazioni batch.
/// </summary>
public interface IWindowService
{
    /// <summary>
    /// Registra la finestra principale dell'applicazione per permettere l'apertura di dialoghi modali.
    /// </summary>
    void RegisterMainWindow(Avalonia.Controls.Window window);

    // =======================================================================
    // 1. ELABORAZIONE GEOMETRICA E METADATI
    // =======================================================================
    
    Task<List<string>?> ShowAlignmentWindowAsync(List<FitsFileReference> sourceFiles, VisualizationMode initialMode = VisualizationMode.Linear);

    Task<FitsHeader?> ShowHeaderEditorAsync(IReadOnlyList<FitsFileReference> files, IImageNavigator navigator);

    Task ShowPlateSolvingWindowAsync(ImageNodeViewModel node);

    // =======================================================================
    // 2. IMPORTAZIONE E CALIBRAZIONE
    // =======================================================================
    
    Task<List<string>?> ShowImportWindowAsync();

    // =======================================================================
    // 3. IMAGE ENHANCEMENT (FILTRI E ANALISI)
    // =======================================================================
    
    Task<List<string>?> ShowPosterizationWindowAsync(List<FitsFileReference> sourceFiles, VisualizationMode initialMode);

    Task<(List<string> Paths, ImageEnhancementMode Mode)?> ShowRadialEnhancementWindowAsync(List<FitsFileReference> files, VisualizationMode mode);

    Task<(List<string> Paths, ImageEnhancementMode Mode)?> ShowStructureExtractionWindowAsync(List<FitsFileReference> sourceFiles, VisualizationMode initialMode);

    Task<(List<string> Paths, ImageEnhancementMode Mode)?> ShowLocalContrastWindowAsync(List<FitsFileReference> sourceFiles, VisualizationMode initialMode);
    
    // =======================================================================
    // 4. ESPORTAZIONE E OUTPUT
    // =======================================================================

    /// <summary>
    /// Mostra la finestra di configurazione interattiva per l'esportazione video.
    /// </summary>
    /// <param name="node">Il nodo immagine sorgente per estrarre risoluzione, frame e anteprima.</param>
    /// <param name="currentMode">La modalità di visualizzazione corrente per sincronizzare l'anteprima.</param>
    /// <returns>
    /// Un oggetto <see cref="VideoExportSettings"/> contenente il percorso scelto, 
    /// il profilo di contrasto della viewport e i parametri di compressione; 
    /// null se l'utente annulla l'operazione.
    /// </returns>
    Task<VideoExportSettings?> ShowVideoExportDialogAsync(ImageNodeViewModel node, VisualizationMode currentMode);
}