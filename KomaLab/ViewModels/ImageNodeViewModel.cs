using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KomaLab.Models.Fits;
using KomaLab.Models.Nodes;
using KomaLab.Models.Visualization;
using KomaLab.Services.Data;        // Necessario per IFitsIoService
using KomaLab.ViewModels.Helpers;

namespace KomaLab.ViewModels;

// ---------------------------------------------------------------------------
// FILE: ImageNodeViewModel.cs
// RUOLO: Classe Base Astratta per Nodi Immagine
// DESCRIZIONE:
// Fornisce l'infrastruttura comune per la gestione della visualizzazione FITS.
// Gestisce:
// 1. Viewport: Coordinate, Zoom e Pan locali alla finestra del nodo.
// 2. Radiometria: Black Point, White Point e Stretching (VisualizationMode).
// 3. Renderer: Ponte tra i dati scientifici (Double) e i pixel visualizzabili (8-bit).
//
// CAMBIAMENTI REFACTORING:
// - Sostituito IFitsService con IFitsIoService in PrepareInputPathsAsync.
// ---------------------------------------------------------------------------

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

    // --- Proprietà Gestione Immagine (Radiometria) ---
    
    [ObservableProperty]
    private VisualizationMode _visualizationMode = VisualizationMode.Linear;

    /// <summary>
    /// Notifica il renderer quando cambia la modalità di stretch (Linear, Log, Sqrt).
    /// </summary>
    partial void OnVisualizationModeChanged(VisualizationMode value)
    {
        if (ActiveRenderer != null)
        {
            ActiveRenderer.VisualizationMode = value;
        }
    }
    
    /// <summary>
    /// Il Renderer attivo (Single o Multiple) che gestisce la trasformazione scientifica -> bitmap.
    /// </summary>
    public abstract FitsRenderer? ActiveRenderer { get; }

    [ObservableProperty] 
    private double _blackPoint;

    [ObservableProperty] 
    private double _whitePoint;

    /// <summary>
    /// Lista dei modi disponibili per popolare i menu di scelta nella UI.
    /// </summary>
    public VisualizationMode[] AvailableVisualizationModes => Enum.GetValues<VisualizationMode>();

    // --- Override BaseNodeViewModel ---

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

        // Esegue l'auto-stretch calcolando i livelli ottimali (solitamente tramite IImageAnalysisService)
        await ActiveRenderer.ResetThresholdsAsync();

        // Sincronizza le proprietà del ViewModel con i risultati del calcolo
        BlackPoint = ActiveRenderer.BlackPoint;
        WhitePoint = ActiveRenderer.WhitePoint;
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
    /// Da chiamare nelle classi derivate quando cambia l'immagine attiva.
    /// Forza la UI a rinfrescare i controlli di contrasto.
    /// </summary>
    protected void NotifyActiveRendererChanged()
    {
        OnPropertyChanged(nameof(ActiveRenderer));
        OnPropertyChanged(nameof(BlackPoint));
        OnPropertyChanged(nameof(WhitePoint));
        OnPropertyChanged(nameof(VisualizationMode)); 
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

    /// <summary>
    /// Prepara i percorsi file necessari per le operazioni di processing esterno.
    /// </summary>
    /// <param name="ioService">Il servizio di I/O Enterprise per gestire eventuali salvataggi temporanei.</param>
    public abstract Task<List<string>> PrepareInputPathsAsync(IFitsIoService ioService);
    
    public abstract Task RefreshDataFromDiskAsync();
}