using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kometra.Models.Fits;
using Kometra.Models.Nodes;
using Kometra.Models.Visualization;
using Kometra.ViewModels.Visualization;

namespace Kometra.ViewModels.Nodes;

/// <summary>
/// Classe base per i nodi FITS. 
/// Gestisce esclusivamente la meccanica della Viewport e lo swap dei renderer.
/// La logica scientifica del contrasto è delegata alle classi derivate.
/// </summary>
public abstract partial class ImageNodeViewModel : BaseNodeViewModel
{
    public ImageViewport Viewport { get; } = new();

    private bool _isFirstLayoutPerformed;
    [ObservableProperty] private Size _viewportSize;

    // --- API Astratta ---
    public abstract FitsRenderer? ActiveRenderer { get; }
    public abstract Size NodeContentSize { get; }
    public abstract IImageNavigator Navigator { get; }

    public VisualizationMode[] AvailableVisualizationModes => Enum.GetValues<VisualizationMode>();
    public override Size EstimatedTotalSize => NodeContentSize;

    protected ImageNodeViewModel(BaseNodeModel model) : base(model) { }

    // ---------------------------------------------------------------------------
    // PROXY PROPERTIES (Sincronizzazione Visuale)
    // ---------------------------------------------------------------------------

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
    // MECCANICA DI SWAP RENDERER
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Esegue lo swap atomico dei renderer gestendo la memoria e la UI.
    /// Non interferisce con il contrasto a meno di richieste esplicite.
    /// </summary>
    protected async Task ApplyNewRendererAsync(FitsRenderer newRenderer, AbsoluteContrastProfile? explicitProfile = null)
    {
        if (newRenderer == null) throw new ArgumentNullException(nameof(newRenderer));

        // 1. SINCRONIZZAZIONE STATO
        // Manteniamo la modalità (Linear/Log) impostata nel nodo
        newRenderer.VisualizationMode = VisualizationMode;

        // Se è stato fornito un profilo esplicito (es. da un editor), lo applichiamo.
        // ALTRIMENTI: Non facciamo nulla. 
        // - Se il nodo figlio ha applicato un Sigma Profile, il renderer è già configurato.
        // - Se è un'immagine nuova, il renderer ha già il suo AutoStretch (da InitializeAsync).
        if (explicitProfile != null)
        {
            newRenderer.ApplyContrastProfile(explicitProfile);
        }

        // 2. SWAP ATOMICO SULLA UI THREAD
        var oldRenderer = ActiveRenderer;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            // Hook per aggiornare la proprietà ActiveFitsImage nel figlio
            OnRendererSwapping(newRenderer);

            // Aggiornamento Viewport
            Viewport.ImageSize = newRenderer.ImageSize;

            // Notifica i binding
            OnPropertyChanged(nameof(ActiveRenderer));
            OnPropertyChanged(nameof(VisualizationMode)); 
            OnPropertyChanged(nameof(NodeContentSize));
            OnPropertyChanged(nameof(EstimatedTotalSize));
        });

        // 3. CLEANUP DIFFERITO
        // Distruggiamo la vecchia Mat OpenCV solo quando la nuova è visualizzata
        if (oldRenderer != null)
        {
            Dispatcher.UIThread.Post(() => oldRenderer.Dispose(), DispatcherPriority.Background);
        }
    }

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
        if (!_isFirstLayoutPerformed && value.Width > 0 && value.Height > 0)
        {
            _isFirstLayoutPerformed = true;
            Dispatcher.UIThread.Post(Viewport.ResetView, DispatcherPriority.Loaded);
        }
    }

    [RelayCommand]
    public async Task ResetThresholdsAsync()
    {
        if (ActiveRenderer != null)
        {
            await ActiveRenderer.ResetThresholdsAsync();
            OnPropertyChanged(nameof(VisualizationMode)); 
        }
    }

    public virtual void ResetView() => Viewport.ResetView();
}