using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KomaLab.Models.Nodes;
using KomaLab.Models.Fits;
using KomaLab.Models.Visualization;
using KomaLab.Services.Data;
using KomaLab.ViewModels.Visualization; // Namespace corretto per FitsRenderer e ImageViewport

namespace KomaLab.ViewModels.Nodes;

// ---------------------------------------------------------------------------
// FILE: ImageNodeViewModel.cs
// RUOLO: ViewModel Base (Gestione Nodi Immagine)
// DESCRIZIONE:
// Classe astratta per nodi che visualizzano immagini (Single o Stack).
// Responsabilità:
// 1. Gestione Viewport: Delega i calcoli geometrici a ImageViewport.
// 2. Ponte UI <-> Renderer: Sincronizza slider (Black/White) con il FitsRenderer attivo.
// 3. Contratto Dati: Definisce i metodi astratti per l'I/O.
// ---------------------------------------------------------------------------

public abstract partial class ImageNodeViewModel : BaseNodeViewModel
{
    // --- Composizione (Viewport) ---
    // ImageViewport gestisce zoom, pan e trasformazioni coordinate.
    public ImageViewport Viewport { get; } = new();

    // --- Proprietà Gestione Vista ---

    // [ObservableProperty] genera: public Size ViewportSize { get; set; }
    // Usiamo il metodo parziale per aggiornare il sottosistema Viewport.
    [ObservableProperty]
    private Size _viewportSize;

    partial void OnViewportSizeChanged(Size value)
    {
        // Propaga il cambiamento di dimensione del controllo UI al motore matematico
        Viewport.ViewportSize = value;
    }

    // --- Proprietà Gestione Immagine (Radiometria) ---
    // Queste proprietà sono bindate ai controlli UI (Slider, ComboBox).
    
    [ObservableProperty]
    private VisualizationMode _visualizationMode = VisualizationMode.Linear;

    [ObservableProperty] 
    private double _blackPoint;

    [ObservableProperty] 
    private double _whitePoint;

    /// <summary>
    /// Riferimento astratto al Renderer attivo. 
    /// Le sottoclassi decidono quale renderer esporre (es. SingleImage espone l'unico che ha).
    /// </summary>
    public abstract FitsRenderer? ActiveRenderer { get; }

    /// <summary>
    /// Helper per popolare le ComboBox nella View.
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

    // --- Logica Sincronizzazione Renderer (Push UI -> Renderer) ---

    // Quando l'utente muove gli slider, aggiorniamo il renderer attivo.
    partial void OnVisualizationModeChanged(VisualizationMode value)
    {
        if (ActiveRenderer != null) ActiveRenderer.VisualizationMode = value;
    }

    partial void OnBlackPointChanged(double value)
    {
        if (ActiveRenderer != null) ActiveRenderer.BlackPoint = value;
    }

    partial void OnWhitePointChanged(double value)
    {
        if (ActiveRenderer != null) ActiveRenderer.WhitePoint = value;
    }

    // --- Comandi (Reset & Auto-Stretch) ---

    [RelayCommand]
    public async Task ResetThresholdsAsync()
    {
        if (ActiveRenderer == null) return;

        // OTTIMIZZAZIONE:
        // 1. Chiediamo al renderer di calcolare i nuovi valori MA di non renderizzare ancora (skipRender: true).
        //    Questo evita uno spreco di CPU.
        await ActiveRenderer.ResetThresholdsAsync(skipRender: true);

        // 2. Aggiorniamo le proprietà del ViewModel.
        //    Questo farà scattare i metodi On...Changed qui sopra.
        //    I metodi On...Changed aggiorneranno il Renderer e triggeranno FINALMENTE il rendering.
        //    Risultato: 1 solo Render invece di 2.
        BlackPoint = ActiveRenderer.BlackPoint;
        WhitePoint = ActiveRenderer.WhitePoint;
    }

    // --- Gestione Zoom/Pan ---

    public void ResetView()
    {
        // Delega alla logica "Smart Fit" implementata in ImageViewport
        Viewport.ResetView();
    }

    // --- Helper per le Sottoclassi (Sync Renderer -> UI) ---

    /// <summary>
    /// Da chiamare nelle classi derivate quando cambia l'immagine attiva (es. caricamento file).
    /// Forza la UI a leggere i valori attuali dal nuovo Renderer.
    /// </summary>
    protected void NotifyActiveRendererChanged()
    {
        // Notifica che l'oggetto Renderer è cambiato
        OnPropertyChanged(nameof(ActiveRenderer));
        
        if (ActiveRenderer != null)
        {
            // Aggiorniamo le proprietà locali (senza preoccuparci troppo del loop, 
            // perché i valori sono identici e il Renderer gestisce il debounce).
            // Usiamo i campi backing field se volessimo evitare il trigger, 
            // ma qui vogliamo assicurarci che la UI sia sincronizzata.
            _blackPoint = ActiveRenderer.BlackPoint;
            _whitePoint = ActiveRenderer.WhitePoint;
            _visualizationMode = ActiveRenderer.VisualizationMode;
        }

        // Notifica alla UI di aggiornare gli slider
        OnPropertyChanged(nameof(BlackPoint));
        OnPropertyChanged(nameof(WhitePoint));
        OnPropertyChanged(nameof(VisualizationMode)); 
    }

    // --- Metodi Astratti (Contratto Dati) ---

    public abstract Task<List<FitsImageData?>> GetCurrentDataAsync();
    
    public abstract FitsImageData? GetActiveImageData();
    
    public abstract Task ApplyProcessedDataAsync(List<FitsImageData> newProcessedData);

    /// <summary>
    /// Prepara i percorsi file necessari per le operazioni di processing esterno.
    /// </summary>
    public abstract Task<List<string>> PrepareInputPathsAsync(IFitsIoService ioService);
    
    public abstract Task RefreshDataFromDiskAsync();
}