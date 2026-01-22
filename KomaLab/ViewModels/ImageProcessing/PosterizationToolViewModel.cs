using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KomaLab.Models.Fits;
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
/// Gestisce la logica di anteprima e l'esecuzione batch rispettando gli header di sessione.
/// </summary>
public partial class PosterizationToolViewModel : ObservableObject, IDisposable
{
    private readonly IFitsDataManager _dataManager;
    private readonly IFitsRendererFactory _rendererFactory;
    private readonly IPosterizationCoordinator _coordinator;

    // Utilizziamo FitsFileReference invece delle semplici stringhe per la coerenza dei metadati
    private readonly List<FitsFileReference> _sourceFiles;
    private CancellationTokenSource? _loadingCts;
    private bool _hasLoadedFirstImage;

    // --- Sottocomponenti ---
    public SequenceNavigator Navigator { get; } = new();
    public ImageViewport Viewport { get; } = new(); 

    [ObservableProperty] 
    [NotifyCanExecuteChangedFor(nameof(ResetThresholdsCommand))]
    private FitsRenderer? _activeRenderer; 

    // Proprietà UI delegate al navigatore
    public string CurrentImageText => $"{Navigator.DisplayIndex} / {_sourceFiles.Count}";
    public bool IsNavigationVisible => Navigator.CanMove;

    [ObservableProperty] private int _levels = 64; 
    [ObservableProperty] private bool _autoAdaptThresholds = true; 
    [ObservableProperty] private bool _isProcessing;
    [ObservableProperty] private string _statusText = "Pronto";

    public VisualizationMode[] AvailableModes => Enum.GetValues<VisualizationMode>();
    
    // Lista dei path risultanti (popolata dopo l'Apply)
    public List<string>? ResultPaths { get; private set; }
    public bool DialogResult { get; private set; }
    public event Action? RequestClose;

    public PosterizationToolViewModel(
        List<FitsFileReference> files, // MODIFICATO: Accetta riferimenti completi
        IFitsDataManager dataManager,
        IFitsRendererFactory rendererFactory,
        IPosterizationCoordinator coordinator)
    {
        _sourceFiles = files ?? throw new ArgumentNullException(nameof(files));
        _dataManager = dataManager;
        _rendererFactory = rendererFactory;
        _coordinator = coordinator;

        // Inizializzazione Navigatore
        Navigator.UpdateStatus(0, _sourceFiles.Count);
        Navigator.IndexChanged += OnNavigatorIndexChanged;

        if (_sourceFiles.Count > 0) 
            _ = LoadImageAtIndexAsync(0);
    }

    private async void OnNavigatorIndexChanged(object? sender, int index)
    {
        OnPropertyChanged(nameof(CurrentImageText));
        await LoadImageAtIndexAsync(index);
    }

    // =======================================================================
    // 1. RENDERING PIPELINE (Async Factory & RAM Optimization)
    // =======================================================================

    private async Task LoadImageAtIndexAsync(int index)
    {
        if (index < 0 || index >= _sourceFiles.Count) return;

        _loadingCts?.Cancel();
        _loadingCts?.Dispose();
        _loadingCts = new CancellationTokenSource();
        var token = _loadingCts.Token;

        StatusText = "Caricamento...";

        try
        {
            var fileRef = _sourceFiles[index];
            
            // 1. Recupero dati grezzi (Cache-aware)
            var data = await _dataManager.GetDataAsync(fileRef.FilePath);
            token.ThrowIfCancellationRequested();

            // 2. CREAZIONE RENDERER CON PRIORITÀ HEADER
            // Usiamo l'header modificato se presente (es. correzioni BSCALE manuali)
            var headerToUse = fileRef.ModifiedHeader ?? data.Header;
            var newRenderer = await _rendererFactory.CreateAsync(data.PixelData, headerToUse);
            
            // 3. Configurazione effetto anteprima
            newRenderer.PostProcessAction = _coordinator.GetPreviewEffect(Levels);

            // 4. LOGICA ADATTIVA (Flicker-Free)
            if (_hasLoadedFirstImage && AutoAdaptThresholds && ActiveRenderer != null)
            {
                // Catturiamo lo stile (K-Sigma) invece della matrice pesante
                var currentStyle = ActiveRenderer.CaptureSigmaProfile();
                token.ThrowIfCancellationRequested();
                
                newRenderer.VisualizationMode = ActiveRenderer.VisualizationMode;
                newRenderer.ApplyRelativeProfile(currentStyle);
            }
            else
            {
                _hasLoadedFirstImage = true;
            }

            // 5. SWAP ATOMICO E PULIZIA RAM
            var old = ActiveRenderer;
            ActiveRenderer = newRenderer;
            
            Viewport.ImageSize = ActiveRenderer.ImageSize;
            StatusText = "Pronto";
            
            // Rilasciamo immediatamente le risorse native del vecchio renderer (Mat OpenCV)
            old?.Dispose();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) 
        { 
            StatusText = $"Errore: {ex.Message}";
            System.Diagnostics.Debug.WriteLine($"[PosterizationTool] Error: {ex}");
        }
    }

    // =======================================================================
    // 2. LOGICA TOOL E BATCH PROCESSING
    // =======================================================================

    partial void OnLevelsChanged(int value)
    {
        if (ActiveRenderer == null) return;
        // Aggiorna l'hook di post-processing e forza il refresh della UI
        ActiveRenderer.PostProcessAction = _coordinator.GetPreviewEffect(value);
        ActiveRenderer.RequestRefresh(); 
    }

    [RelayCommand(CanExecute = nameof(CanInteract))]
    private async Task ResetThresholds()
    {
        if (ActiveRenderer != null)
        {
            await ActiveRenderer.ResetThresholdsAsync();
            // Notifica manuale necessaria per i binding del Black/White point se non automatici
            OnPropertyChanged(nameof(ActiveRenderer)); 
        }
    }

    private bool CanInteract() => ActiveRenderer != null && !IsProcessing;

    [RelayCommand]
    private async Task Apply()
    {
        if (IsProcessing || ActiveRenderer == null) return;
        
        Navigator.Stop(); 
        IsProcessing = true;
        
        try
        {
            var progress = new Progress<BatchProgressReport>(p => 
                StatusText = $"Elaborazione: {p.CurrentFileIndex}/{p.TotalFiles}");

            // Il coordinator riceverà la lista di file completa di metadati aggiornati
            ResultPaths = await _coordinator.ExecuteBatchAsync(
                _sourceFiles, 
                Levels, 
                ActiveRenderer.VisualizationMode, 
                ActiveRenderer.BlackPoint, 
                ActiveRenderer.WhitePoint, 
                progress);

            DialogResult = true;
            RequestClose?.Invoke();
        }
        catch (Exception ex) 
        { 
            StatusText = $"Errore applicazione: {ex.Message}"; 
            IsProcessing = false; 
        }
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