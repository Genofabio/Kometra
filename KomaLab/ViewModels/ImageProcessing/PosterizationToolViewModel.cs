using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KomaLab.Models.Fits;
using KomaLab.Models.Fits.Structure;
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

    private readonly List<FitsFileReference> _sourceFiles;
    private CancellationTokenSource? _loadingCts;
    private bool _hasLoadedFirstImage;

    // --- Sottocomponenti ---
    public SequenceNavigator Navigator { get; } = new();
    public ImageViewport Viewport { get; } = new(); 

    [ObservableProperty] 
    [NotifyCanExecuteChangedFor(nameof(ResetThresholdsCommand))]
    private FitsRenderer? _activeRenderer; 

    // --- PROPRIETÀ PROXY: IL RENDERER COMANDA ---

    public double BlackPoint
    {
        get => ActiveRenderer?.BlackPoint ?? 0;
        set
        {
            if (ActiveRenderer == null || Math.Abs(ActiveRenderer.BlackPoint - value) < 0.1) return;
            
            // Logica Pushing: se il nero supera il bianco, sposta il bianco in avanti
            if (value >= WhitePoint)
            {
                ActiveRenderer.WhitePoint = Math.Clamp(value + 1.0, 0, 65535);
                OnPropertyChanged(nameof(WhitePoint));
            }

            ActiveRenderer.BlackPoint = Math.Clamp(value, 0, 65535);
            OnPropertyChanged();
        }
    }

    public double WhitePoint
    {
        get => ActiveRenderer?.WhitePoint ?? 65535;
        set
        {
            if (ActiveRenderer == null || Math.Abs(ActiveRenderer.WhitePoint - value) < 0.1) return;

            // Logica Pushing: se il bianco scende sotto il nero, sposta il nero all'indietro
            if (value <= BlackPoint)
            {
                ActiveRenderer.BlackPoint = Math.Clamp(value - 1.0, 0, 65535);
                OnPropertyChanged(nameof(BlackPoint));
            }

            ActiveRenderer.WhitePoint = Math.Clamp(value, 0, 65535);
            OnPropertyChanged();
        }
    }

    public VisualizationMode SelectedMode
    {
        get => ActiveRenderer?.VisualizationMode ?? VisualizationMode.Linear;
        set
        {
            if (ActiveRenderer != null)
            {
                ActiveRenderer.VisualizationMode = value;
                OnPropertyChanged();
            }
        }
    }

    [ObservableProperty] private int _levels = 64; 
    [ObservableProperty] private int _maxLevels = 64; 
    [ObservableProperty] private bool _autoAdaptThresholds = true; 
    [ObservableProperty] private bool _isProcessing;
    [ObservableProperty] private string _statusText = "Pronto";

    // Proprietà UI delegate al navigatore
    public VisualizationMode[] AvailableModes => Enum.GetValues<VisualizationMode>();
    public string CurrentImageText => $"{Navigator.DisplayIndex} / {_sourceFiles.Count}";
    public bool IsNavigationVisible => Navigator.CanMove;
    public List<string>? ResultPaths { get; private set; }
    public bool DialogResult { get; private set; }
    public event Action? RequestClose;

    public PosterizationToolViewModel(
        List<FitsFileReference> files,
        IFitsDataManager dataManager,
        IFitsRendererFactory rendererFactory,
        IPosterizationCoordinator coordinator)
    {
        _sourceFiles = files;
        _dataManager = dataManager;
        _rendererFactory = rendererFactory;
        _coordinator = coordinator;

        Navigator.UpdateStatus(0, _sourceFiles.Count);
        Navigator.IndexChanged += async (_, idx) => await LoadImageAtIndexAsync(idx);

        if (_sourceFiles.Count > 0) 
            _ = LoadImageAtIndexAsync(0);
    }

    // =======================================================================
    // 1. RENDERING PIPELINE (Flicker-Free & Adaptive)
    // =======================================================================

    private async Task LoadImageAtIndexAsync(int index)
    {
        _loadingCts?.Cancel();
        _loadingCts = new CancellationTokenSource();
        var token = _loadingCts.Token;

        try
        {
            StatusText = "Caricamento...";
            var fileRef = _sourceFiles[index];
            var data = await _dataManager.GetDataAsync(fileRef.FilePath);
            token.ThrowIfCancellationRequested();

            // [MODIFICA MEF] Accesso sicuro all'HDU immagine
            var imageHdu = data.FirstImageHdu ?? data.PrimaryHdu;
            if (imageHdu == null) 
                throw new InvalidOperationException("Nessuna immagine valida trovata nel file FITS.");

            var newRenderer = await _rendererFactory.CreateAsync(imageHdu.PixelData, fileRef.ModifiedHeader ?? imageHdu.Header);
            
            // --- DEFAULT LIVELLI DINAMICO (Solo al primo avvio) ---
            if (!_hasLoadedFirstImage)
            {
                // Se l'immagine è Float/Double, partiamo da 32 livelli, altrimenti 64
                if (newRenderer.RenderBitDepth == FitsBitDepth.Float || 
                    newRenderer.RenderBitDepth == FitsBitDepth.Double)
                {
                    Levels = 32;
                    MaxLevels = 32;
                }
                else
                {
                    Levels = 64;
                    MaxLevels = 64;
                }
            }

            // Configurazione hook anteprima
            newRenderer.PostProcessAction = _coordinator.GetPreviewEffect(Levels);

            if (_hasLoadedFirstImage && ActiveRenderer != null)
            {
                newRenderer.VisualizationMode = ActiveRenderer.VisualizationMode;

                if (AutoAdaptThresholds)
                {
                    // Adattamento statistico dinamico (K-Sigma)
                    newRenderer.ApplyRelativeProfile(ActiveRenderer.CaptureSigmaProfile());
                }
                else
                {
                    // Mantenimento valori ADU fissi
                    newRenderer.BlackPoint = ActiveRenderer.BlackPoint;
                    newRenderer.WhitePoint = ActiveRenderer.WhitePoint;
                }
            }

            var old = ActiveRenderer;
            ActiveRenderer = newRenderer;
            
            // Sincronizzazione UI
            OnPropertyChanged(nameof(BlackPoint));
            OnPropertyChanged(nameof(WhitePoint));
            OnPropertyChanged(nameof(SelectedMode));
            OnPropertyChanged(nameof(CurrentImageText));

            Viewport.ImageSize = ActiveRenderer.ImageSize;
            
            // Gestione dimensioni iniziali finestra e prima attivazione
            if (!_hasLoadedFirstImage) 
            { 
                _hasLoadedFirstImage = true;
                await Task.Delay(50); 
                Viewport.ResetView(); 
            }

            StatusText = "Pronto";
            old?.Dispose();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) 
        { 
            StatusText = $"Errore: {ex.Message}"; 
        }
    }

    // =======================================================================
    // 2. LOGICA TOOL E BATCH
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
        {
            await ActiveRenderer.ResetThresholdsAsync();
            OnPropertyChanged(nameof(BlackPoint));
            OnPropertyChanged(nameof(WhitePoint));
        }
    }

    private bool CanInteract() => ActiveRenderer != null && !IsProcessing;

    [RelayCommand]
    private async Task Apply()
    {
        if (IsProcessing || ActiveRenderer == null) return;
        
        IsProcessing = true;
        try
        {
            // Catturiamo il profilo sigma corrente per l'adattamento batch
            var currentProfile = ActiveRenderer.CaptureSigmaProfile();

            var progress = new Progress<BatchProgressReport>(p => 
                StatusText = $"Elaborazione: {p.CurrentFileIndex}/{p.TotalFiles}");

            // Esecuzione batch con parametri di adattamento definitivi
            ResultPaths = await _coordinator.ExecuteBatchAsync(
                _sourceFiles, 
                Levels, 
                ActiveRenderer.VisualizationMode, 
                ActiveRenderer.BlackPoint, 
                ActiveRenderer.WhitePoint,
                AutoAdaptThresholds,
                currentProfile,
                progress);

            DialogResult = true;
            RequestClose?.Invoke();
        }
        catch (Exception ex) 
        { 
            StatusText = $"Errore: {ex.Message}";
            IsProcessing = false; 
        }
    }

    [RelayCommand] private void Cancel() => RequestClose?.Invoke();

    public void Dispose()
    {
        _loadingCts?.Cancel();
        _loadingCts?.Dispose();
        ActiveRenderer?.Dispose();
    }
}