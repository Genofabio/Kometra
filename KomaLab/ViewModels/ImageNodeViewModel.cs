using System; // Necessario per Enum.GetValues
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KomaLab.Models;
using KomaLab.Services.Data;
using KomaLab.ViewModels.Helpers;

namespace KomaLab.ViewModels;

/// <summary>
/// Classe base per tutti i nodi che visualizzano o manipolano immagini FITS.
/// Gestisce la logica comune di:
/// 1. Geometria (Viewport, Zoom, Pan)
/// 2. Radiometria (Black/White Point, Reset Soglie, Visualization Mode)
/// 3. Interfaccia con il Renderer attivo
/// </summary>
public abstract partial class ImageNodeViewModel : BaseNodeViewModel
{
    // --- Campi Privati ---
    private Size _viewportSize;

    // --- Proprietà Gestione Vista ---
    public ImageViewport Viewport { get; } = new();

    public Size ViewportSize
    {
        get => _viewportSize;
        set
        {
            if (_viewportSize != value)
            {
                _viewportSize = value;
                Viewport.ViewportSize = value;
            }
        }
    }

    // --- Proprietà Gestione Immagine ---
    
    [ObservableProperty]
    private VisualizationMode _visualizationMode = Models.VisualizationMode.Linear;

    // Quando l'utente cambia la modalià dal menu/UI:
    partial void OnVisualizationModeChanged(VisualizationMode value)
    {
        // Imponiamo subito la scelta al renderer attivo (se esiste)
        if (ActiveRenderer != null)
        {
            ActiveRenderer.VisualizationMode = value;
        }
    }
    
    public abstract FitsRenderer? ActiveRenderer { get; }

    [ObservableProperty] 
    private double _blackPoint;

    [ObservableProperty] 
    private double _whitePoint;

    /// <summary>
    /// Lista dei modi disponibili per popolare la ComboBox.
    /// </summary>
    public VisualizationMode[] AvailableVisualizationModes => Enum.GetValues<VisualizationMode>();

    // --- Override BaseNodeViewModel ---

    public override NodeCategory Category => NodeCategory.Image;

    public override Size EstimatedTotalSize
    {
        get
        {
            var contentSize = this.NodeContentSize;
            return new Size(contentSize.Width, contentSize.Height);
        }
    }

    protected abstract Size NodeContentSize { get; }

    // --- Costruttore ---

    protected ImageNodeViewModel(BaseNodeModel model) : base(model)
    {
    }

    // --- Gestione Soglie (Condivisa) ---

    [RelayCommand]
    public async Task ResetThresholdsAsync()
    {
        if (ActiveRenderer == null) return;

        await ActiveRenderer.ResetThresholdsAsync();

        BlackPoint = ActiveRenderer.BlackPoint;
        WhitePoint = ActiveRenderer.WhitePoint;
        // Nota: Il VisualizationMode solitamente non si resetta con l'auto-stretch, 
        // ma se volessi forzarlo a Linear, lo faresti qui.
    }

    partial void OnBlackPointChanged(double value)
    {
        if (ActiveRenderer != null) ActiveRenderer.BlackPoint = value;
    }

    partial void OnWhitePointChanged(double value)
    {
        if (ActiveRenderer != null) ActiveRenderer.WhitePoint = value;
    }

    // --- Helper per le Sottoclassi (Sync UI) ---

    /// <summary>
    /// Da chiamare nelle classi derivate (es. MultipleImagesNode) quando
    /// cambia l'immagine visualizzata (ActiveRenderer cambia).
    /// Forza la UI a rileggere tutte le proprietà visive dal nuovo renderer.
    /// </summary>
    protected void NotifyActiveRendererChanged()
    {
        OnPropertyChanged(nameof(ActiveRenderer));
        OnPropertyChanged(nameof(BlackPoint));
        OnPropertyChanged(nameof(WhitePoint));
        OnPropertyChanged(nameof(VisualizationMode)); // Aggiorna la ComboBox col valore della nuova immagine
    }

    // --- Gestione Zoom/Pan ---

    public void ResetView()
    {
        Viewport.Scale = 1.0;
        Viewport.OffsetX = 0;
        Viewport.OffsetY = 0;
    }

    // --- Metodi Astratti (Contratto Dati) ---

    public abstract Task<List<FitsImageData?>> GetCurrentDataAsync();
    public abstract FitsImageData? GetActiveImageData();
    public abstract Task ApplyProcessedDataAsync(List<FitsImageData> newProcessedData);
    public abstract Task<List<string>> PrepareInputPathsAsync(IFitsService fitsService);
    public abstract Task RefreshDataFromDiskAsync();
}