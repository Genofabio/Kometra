using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KomaLab.Models.Processing;
using KomaLab.Models.Visualization;
using KomaLab.Services.Factories;
using KomaLab.Services.Fits;
using KomaLab.Services.Processing.Coordinators;
using KomaLab.ViewModels.Visualization;
using SequenceNavigator = KomaLab.ViewModels.Shared.SequenceNavigator;

namespace KomaLab.ViewModels.ImageProcessing;

/// <summary>
/// ViewModel per il Tool di Posterizzazione. 
/// Utilizza Async Factory per il caricamento sicuro e coerente delle immagini FITS.
/// </summary>
public partial class PosterizationToolViewModel : ObservableObject, IDisposable
{
    private readonly IFitsDataManager _dataManager;
    private readonly IFitsRendererFactory _rendererFactory;
    private readonly IPosterizationCoordinator _coordinator;

    private readonly List<string> _sourcePaths;
    private CancellationTokenSource? _loadingCts;
    private bool _hasLoadedFirstImage;

    // --- Sottocomponenti Coerenti ---
    public SequenceNavigator Navigator { get; } = new();
    public ImageViewport Viewport { get; } = new(); 

    [ObservableProperty] 
    [NotifyCanExecuteChangedFor(nameof(ResetThresholdsCommand))]
    private FitsRenderer? _activeRenderer; 

    // Proprietà UI delegate al navigatore
    public string CurrentImageText => $"{Navigator.DisplayIndex} / {_sourcePaths.Count}";
    public bool IsNavigationVisible => Navigator.CanMove;

    [ObservableProperty] private int _levels = 64; 
    [ObservableProperty] private bool _autoAdaptThresholds = true; 
    [ObservableProperty] private bool _isProcessing;
    [ObservableProperty] private string _statusText = "Pronto";

    public VisualizationMode[] AvailableModes => Enum.GetValues<VisualizationMode>();
    public List<string>? ResultPaths { get; private set; }
    public bool DialogResult { get; private set; }
    public event Action? RequestClose;

    public PosterizationToolViewModel(
        List<string> paths,
        IFitsDataManager dataManager,
        IFitsRendererFactory rendererFactory,
        IPosterizationCoordinator coordinator)
    {
        _sourcePaths = paths ?? new List<string>();
        _dataManager = dataManager;
        _rendererFactory = rendererFactory;
        _coordinator = coordinator;

        // Inizializzazione Navigatore
        Navigator.UpdateStatus(0, _sourcePaths.Count);
        Navigator.IndexChanged += OnNavigatorIndexChanged;

        if (_sourcePaths.Count > 0) 
            _ = LoadImageAtIndexAsync(0);
    }

    private async void OnNavigatorIndexChanged(object? sender, int index)
    {
        OnPropertyChanged(nameof(CurrentImageText));
        await LoadImageAtIndexAsync(index);
    }

    // =======================================================================
    // 1. RENDERING PIPELINE (Async Factory Pattern)
    // =======================================================================

    private async Task LoadImageAtIndexAsync(int index)
    {
        if (index < 0 || index >= _sourcePaths.Count) return;

        _loadingCts?.Cancel();
        _loadingCts?.Dispose();
        _loadingCts = new CancellationTokenSource();
        var token = _loadingCts.Token;

        StatusText = "Caricamento...";

        try
        {
            // 1. Caricamento dati grezzi
            var data = await _dataManager.GetDataAsync(_sourcePaths[index]);
            token.ThrowIfCancellationRequested();

            // 2. ASYNC FACTORY CREATION
            // Restituisce un renderer già inizializzato e pronto (Mat valida).
            var newRenderer = await _rendererFactory.CreateAsync(data.PixelData, data.Header);
            
            // 3. Configurazione specifica del Tool
            // Applichiamo l'effetto di posterizzazione come hook post-stretch
            newRenderer.PostProcessAction = _coordinator.GetPreviewEffect(Levels);

            // 4. Logica Adattiva (Sicura: newRenderer è valido)
            if (_hasLoadedFirstImage && AutoAdaptThresholds && ActiveRenderer != null)
            {
                using var nextMat = newRenderer.CaptureScientificMat(); // ORA È SICURO
                token.ThrowIfCancellationRequested();
                
                // Copia soglie e modalità visualizzazione
                newRenderer.VisualizationMode = ActiveRenderer.VisualizationMode;
                var profile = ActiveRenderer.GetAdaptedProfileFor(nextMat);
                newRenderer.ApplyContrastProfile(profile);
            }
            else
            {
                _hasLoadedFirstImage = true;
            }

            // 5. Swap Atomico
            var old = ActiveRenderer;
            ActiveRenderer = newRenderer;
            
            // Sincronizza Viewport
            Viewport.ImageSize = ActiveRenderer.ImageSize;
            StatusText = "Pronto";
            
            // Cleanup differito
            old?.Dispose();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusText = $"Errore: {ex.Message}"; }
    }

    // =======================================================================
    // 2. LOGICA TOOL E COMANDI
    // =======================================================================

    partial void OnLevelsChanged(int value)
    {
        if (ActiveRenderer == null) return;
        ActiveRenderer.PostProcessAction = _coordinator.GetPreviewEffect(value);
        ActiveRenderer.RequestRefresh(); 
    }

    [RelayCommand(CanExecute = nameof(CanInteract))]
    private async Task ResetThresholds()
    {
        if (ActiveRenderer != null)
            await ActiveRenderer.ResetThresholdsAsync();
    }

    private bool CanInteract() => ActiveRenderer != null && !IsProcessing;

    [RelayCommand]
    private async Task Apply()
    {
        if (IsProcessing || ActiveRenderer == null) return;
        
        Navigator.Stop(); // Fermiamo l'animazione durante il batch
        IsProcessing = true;
        
        try
        {
            var progress = new Progress<BatchProgressReport>(p => 
                StatusText = $"Elaborazione: {p.CurrentFileIndex}/{p.TotalFiles}");

            ResultPaths = await _coordinator.ExecuteBatchAsync(
                _sourcePaths, 
                Levels, 
                ActiveRenderer.VisualizationMode, 
                ActiveRenderer.BlackPoint, 
                ActiveRenderer.WhitePoint, 
                progress);

            DialogResult = true;
            RequestClose?.Invoke();
        }
        catch (Exception ex) { StatusText = $"Errore: {ex.Message}"; IsProcessing = false; }
    }

    [RelayCommand] private void Cancel() => RequestClose?.Invoke();

    // =======================================================================
    // 3. CLEANUP
    // =======================================================================

    public void Dispose() 
    { 
        Navigator.Stop();
        Navigator.IndexChanged -= OnNavigatorIndexChanged;
        _loadingCts?.Cancel();
        _loadingCts?.Dispose();
        ActiveRenderer?.Dispose();
        GC.SuppressFinalize(this);
    }
}