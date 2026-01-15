using System;
using System.Collections.Generic;
using System.Linq; // Aggiunto per LINQ
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KomaLab.Models.Nodes; // Assumo BaseNodeViewModel sia qui
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
    
    // Il Renderer attivo che disegna i pixel (già aggiornato alla nuova architettura)
    public abstract FitsRenderer? ActiveRenderer { get; }
    
    // Dimensione del nodo UI
    public abstract Size NodeContentSize { get; }
    
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
        if (newRenderer == null) throw new ArgumentNullException(nameof(newRenderer));

        // 1. Preparazione (Pesante, in background)
        // Il renderer carica la matrice OpenCV qui dentro
        await newRenderer.InitializeAsync();
        newRenderer.VisualizationMode = this.VisualizationMode;

        // Gestione intelligenza continuità contrasto
        if (explicitProfile != null)
        {
            newRenderer.ApplyContrastProfile(explicitProfile);
        }
        else if (ActiveRenderer != null)
        {
            // Se stiamo solo cambiando immagine nello stesso nodo, preserviamo lo stretch
            newRenderer.ApplyContrastProfile(ActiveRenderer.CaptureContrastProfile());
        }

        // 2. Lo SWAP (Deve essere atomico per la UI)
        var oldRenderer = ActiveRenderer; // Salviamo il VECCHIO riferimento

        // Eseguiamo il cambio sulla UI Thread
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            // Template Method: La sottoclasse aggiorna la sua proprietà Observable (es. _fitsImage)
            OnRendererSwapping(newRenderer);

            // Sincronizzazione parametri Viewport e Istogramma
            Viewport.ImageSize = newRenderer.ImageSize;
            
            // Aggiorniamo le proprietà osservabili del ViewModel per riflettere il nuovo renderer
            BlackPoint = newRenderer.BlackPoint;
            WhitePoint = newRenderer.WhitePoint;

            OnPropertyChanged(nameof(NodeContentSize));
            OnPropertyChanged(nameof(EstimatedTotalSize));
        });

        // 3. Cleanup sicuro (Posticipato)
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
        
        // Aggiorniamo le proprietà bindate alla UI
        BlackPoint = ActiveRenderer.BlackPoint;
        WhitePoint = ActiveRenderer.WhitePoint;
    }

    public virtual void ResetView()
    {
        Viewport.ResetView();
    }

    // --- Contratti Dati Aggiornati (No FitsImageData) ---

    /// <summary>
    /// Restituisce la collezione di dati gestita da questo nodo.
    /// Sostituisce il vecchio GetCurrentDataAsync().
    /// </summary>
    public abstract FitsCollection? OutputCollection { get; }

    /// <summary>
    /// Restituisce il riferimento al file attualmente visualizzato nel renderer.
    /// Sostituisce GetActiveImageData().
    /// </summary>
    public abstract FitsFileReference? ActiveFile { get; }

    /// <summary>
    /// Riceve una nuova collezione in input (es. dal nodo precedente) e la carica.
    /// Sostituisce ApplyProcessedDataAsync().
    /// </summary>
    public abstract Task LoadInputAsync(FitsCollection input);
    
    /// <summary>
    /// Forza il ricaricamento dei dati dal disco (utile se i file sono stati sovrascritti).
    /// </summary>
    public abstract Task RefreshDataFromDiskAsync();

    /// <summary>
    /// Restituisce la lista dei path gestiti (helper per operazioni batch).
    /// </summary>
    public virtual List<string> GetManagedFilePaths()
    {
        return OutputCollection?.Files.Select(f => f.FilePath).ToList() ?? new List<string>();
    }
}