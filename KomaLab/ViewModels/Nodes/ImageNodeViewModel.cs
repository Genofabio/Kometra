using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KomaLab.Models.Nodes;
using KomaLab.Models.Fits;
using KomaLab.Models.Visualization;
using KomaLab.Services.Fits;
using KomaLab.ViewModels.Visualization;

namespace KomaLab.ViewModels.Nodes;

/// <summary>
/// ViewModel di base per tutti i nodi che visualizzano dati FITS.
/// Centralizza la logica di Viewport, trasformazioni radiometriche e gestione del ciclo di vita dei Renderer.
/// </summary>
public abstract partial class ImageNodeViewModel : BaseNodeViewModel
{
    // --- Composizione ---
    public ImageViewport Viewport { get; } = new();

    // --- Stato Layout ---
    private bool _isFirstLayoutPerformed;

    [ObservableProperty]
    private Size _viewportSize;

    // --- Proprietà Radiometriche (Sincronizzate con il Renderer attivo) ---
    [ObservableProperty]
    private VisualizationMode _visualizationMode = VisualizationMode.Linear;

    [ObservableProperty] 
    private double _blackPoint;

    [ObservableProperty] 
    private double _whitePoint;

    // --- Abstract API (Contratto per le sottoclassi) ---
    public abstract FitsRenderer? ActiveRenderer { get; }
    protected abstract Size NodeContentSize { get; }
    public VisualizationMode[] AvailableVisualizationModes => Enum.GetValues<VisualizationMode>();

    // --- Override BaseNodeViewModel ---
    public override Size EstimatedTotalSize => NodeContentSize;

    protected ImageNodeViewModel(BaseNodeModel model) : base(model) { }

    // --- Logica Centralizzata di Swap Renderer ---

    /// <summary>
    /// Gestisce in modo sicuro lo scambio tra un vecchio e un nuovo Renderer.
    /// Si occupa di inizializzazione, trasferimento profili di contrasto, cleanup e notifiche UI.
    /// </summary>
    protected async Task ApplyNewRendererAsync(FitsRenderer newRenderer, ContrastProfile? explicitProfile = null)
    {
        // 1. Preparazione (Pesante, in background)
        await newRenderer.InitializeAsync();
        newRenderer.VisualizationMode = this.VisualizationMode;

        if (explicitProfile != null)
            newRenderer.ApplyContrastProfile(explicitProfile);
        else if (ActiveRenderer != null)
            newRenderer.ApplyContrastProfile(ActiveRenderer.CaptureContrastProfile());

        // 2. Lo SWAP (Deve essere atomico per la UI)
        var oldRenderer = ActiveRenderer; // Salviamo il VECCHIO riferimento

        // Eseguiamo il cambio sulla UI Thread
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            OnRendererSwapping(newRenderer);

            // Sincronizzazione parametri
            Viewport.ImageSize = newRenderer.ImageSize;
            BlackPoint = newRenderer.BlackPoint;
            WhitePoint = newRenderer.WhitePoint;

            OnPropertyChanged(nameof(NodeContentSize));
            OnPropertyChanged(nameof(EstimatedTotalSize));
        });

        // 3. Cleanup sicuro (Posticipato per evitare NullReference durante il Measure pass)
        if (oldRenderer != null)
        {
            // Aspettiamo che il ciclo di rendering corrente finisca prima di distruggere i buffer
            Dispatcher.UIThread.Post(() => oldRenderer.Dispose(), DispatcherPriority.Background);
        }
    }

    /// <summary>
    /// Obbliga la sottoclasse ad assegnare il renderer alla propria proprietà specifica (es. FitsImage).
    /// </summary>
    protected abstract void OnRendererSwapping(FitsRenderer newRenderer);

    // --- Gestione Ciclo di Vita Layout ---

    partial void OnViewportSizeChanged(Size value)
    {
        Viewport.ViewportSize = value;
    
        // Fix "Tempo Zero": Esegue il reset solo quando la UI ha dimensioni reali.
        if (!_isFirstLayoutPerformed && value.Width > 0 && value.Height > 0)
        {
            _isFirstLayoutPerformed = true;
            Dispatcher.UIThread.Post(ResetView, DispatcherPriority.Loaded);
        }

        OnViewportSizeUpdated(value);
    }

    /// <summary>
    /// Hook opzionale per reagire a cambiamenti di dimensione della Viewport.
    /// </summary>
    protected virtual void OnViewportSizeUpdated(Size newSize) { }

    // --- Logica Sincronizzazione UI -> Renderer ---

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

    // --- Comandi ---

    [RelayCommand]
    public async Task ResetThresholdsAsync()
    {
        if (ActiveRenderer == null) return;
        
        await ActiveRenderer.ResetThresholdsAsync(skipRegeneration: true);
        
        BlackPoint = ActiveRenderer.BlackPoint;
        WhitePoint = ActiveRenderer.WhitePoint;
    }

    public virtual void ResetView()
    {
        Viewport.ResetView();
    }

    // --- Contratti Dati (I/O) ---

    public abstract Task<List<FitsImageData?>> GetCurrentDataAsync();
    public abstract FitsImageData? GetActiveImageData();
    public abstract Task ApplyProcessedDataAsync(List<FitsImageData> newProcessedData);
    public abstract Task<List<string>> PrepareInputPathsAsync(IFitsIoService ioService);
    public abstract Task RefreshDataFromDiskAsync();
}