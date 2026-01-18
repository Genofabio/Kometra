using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KomaLab.Models.Nodes;
using KomaLab.Models.Fits;
using KomaLab.Models.Visualization;
using KomaLab.ViewModels.Visualization;

namespace KomaLab.ViewModels.Nodes;

/// <summary>
/// Classe base per i nodi FITS. 
/// Gestisce la composizione della Viewport, lo swap dei renderer e definisce 
/// il contratto per la navigazione sequenziale delle immagini.
/// </summary>
public abstract partial class ImageNodeViewModel : BaseNodeViewModel
{
    // --- Composizione ---
    public ImageViewport Viewport { get; } = new();

    // --- Stato Layout ---
    private bool _isFirstLayoutPerformed;
    [ObservableProperty] private Size _viewportSize;

    // ---------------------------------------------------------------------------
    // ABSTRACT API (Implementata dai figli)
    // ---------------------------------------------------------------------------

    /// <summary> L'istanza attiva del renderer (Source of Truth per i pixel). </summary>
    public abstract FitsRenderer? ActiveRenderer { get; }
    
    /// <summary> La dimensione effettiva dell'immagine caricata. </summary>
    public abstract Size NodeContentSize { get; }
    
    /// <summary> 
    /// Il navigatore della sequenza. Obbligatorio per garantire coerenza 
    /// tra nodi singoli e multipli nella UI.
    /// </summary>
    public abstract IImageNavigator Navigator { get; }

    public VisualizationMode[] AvailableVisualizationModes => Enum.GetValues<VisualizationMode>();
    public override Size EstimatedTotalSize => NodeContentSize;

    protected ImageNodeViewModel(BaseNodeModel model) : base(model) { }

    // ---------------------------------------------------------------------------
    // PROXY PROPERTIES (Binding Helpers)
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Proxy verso la modalità di visualizzazione del renderer attivo.
    /// </summary>
    public VisualizationMode VisualizationMode
    {
        get => ActiveRenderer?.VisualizationMode ?? VisualizationMode.Linear;
        set
        {
            if (ActiveRenderer != null && ActiveRenderer.VisualizationMode != value)
            {
                ActiveRenderer.VisualizationMode = value;
                OnPropertyChanged();
            }
        }
    }

    // ---------------------------------------------------------------------------
    // LOGICA DI SWAP RENDERER
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Esegue lo swap sicuro tra renderer, gestendo l'adattamento del contrasto.
    /// <para>
    /// PRE-CONDIZIONE: <paramref name="newRenderer"/> DEVE essere già inizializzato 
    /// (tramite Factory Async) e pronto all'uso (Mat valida).
    /// </para>
    /// </summary>
    protected async Task ApplyNewRendererAsync(FitsRenderer newRenderer, AbsoluteContrastProfile? explicitProfile = null)
    {
        if (newRenderer == null) throw new ArgumentNullException(nameof(newRenderer));

        // NOTA ARCHITETTURALE: 
        // Non chiamiamo più `await newRenderer.InitializeAsync()` qui.
        // Si assume che la Factory abbia già restituito un oggetto valido e idratato.

        // 1. Sincronizzazione dello stato radiometrico e visuale
        if (explicitProfile != null)
        {
            newRenderer.VisualizationMode = VisualizationMode; // Mantiene la modalità corrente
            newRenderer.ApplyContrastProfile(explicitProfile);
        }
        else if (ActiveRenderer != null)
        {
            // Ereditiamo lo stato dal renderer precedente (flicker-free experience)
            newRenderer.VisualizationMode = VisualizationMode;
            newRenderer.ApplyContrastProfile(ActiveRenderer.CaptureContrastProfile());
        }

        // 2. Swap atomico sulla UI Thread
        var oldRenderer = ActiveRenderer;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            OnRendererSwapping(newRenderer);

            // Sincronizzazione Viewport
            Viewport.ImageSize = newRenderer.ImageSize;

            // Trigger notifiche per i binding Avalonia
            OnPropertyChanged(nameof(ActiveRenderer));
            OnPropertyChanged(nameof(VisualizationMode)); 
            OnPropertyChanged(nameof(NodeContentSize));
            OnPropertyChanged(nameof(EstimatedTotalSize));
        });

        // 3. Cleanup differito per non bloccare il rendering
        if (oldRenderer != null)
        {
            Dispatcher.UIThread.Post(() => oldRenderer.Dispose(), DispatcherPriority.Background);
        }
    }

    /// <summary> Metodo hook per aggiornare il riferimento locale nel figlio. </summary>
    protected abstract void OnRendererSwapping(FitsRenderer newRenderer);

    // ---------------------------------------------------------------------------
    // CONTRATTI DATI
    // ---------------------------------------------------------------------------

    public abstract IReadOnlyList<FitsFileReference> CurrentFiles { get; }
    public abstract FitsFileReference? ActiveFile { get; }
    public abstract Task LoadInputAsync(IEnumerable<FitsFileReference> input);
    public abstract Task RefreshDataFromDiskAsync();
    
    public List<string> GetManagedFilePaths() => CurrentFiles.Select(f => f.FilePath).ToList();

    // ---------------------------------------------------------------------------
    // LAYOUT E COMANDI
    // ---------------------------------------------------------------------------

    partial void OnViewportSizeChanged(Size value)
    {
        Viewport.ViewportSize = value;
        
        // Auto-Reset dello zoom al primo layout valido della board
        if (!_isFirstLayoutPerformed && value.Width > 0 && value.Height > 0)
        {
            _isFirstLayoutPerformed = true;
            Dispatcher.UIThread.Post(Viewport.ResetView, DispatcherPriority.Loaded);
        }
    }

    [RelayCommand]
    public async Task ResetThresholdsAsync()
    {
        if (ActiveRenderer != null) await ActiveRenderer.ResetThresholdsAsync();
    }

    public virtual void ResetView() => Viewport.ResetView();
}