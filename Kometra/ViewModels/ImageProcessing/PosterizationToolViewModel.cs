using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kometra.Infrastructure; // Aggiunto per localizzazione
using Kometra.Models.Fits;
using Kometra.Models.Fits.Structure;
using Kometra.Models.Processing;
using Kometra.Models.Processing.Batch;
using Kometra.Models.Visualization;
using Kometra.Services.Factories;
using Kometra.Services.Fits;
using Kometra.Services.Processing.Coordinators;
using Kometra.ViewModels.Visualization;
using SequenceNavigator = Kometra.ViewModels.Shared.SequenceNavigator;

namespace Kometra.ViewModels.ImageProcessing;

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
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private FitsRenderer? _activeRenderer; 

    // --- LIMITI DINAMICI PER LA UI ---
    [ObservableProperty] private double _sliderMin = 0;
    [ObservableProperty] private double _sliderMax = 65535;

    // --- PROPRIETÀ PROXY: IL RENDERER COMANDA ---

    public double BlackPoint
    {
        get => ActiveRenderer?.BlackPoint ?? 0;
        set
        {
            // FIX BUG 2: Ignora gli aggiornamenti Two-Way dalla UI mentre l'immagine sta caricando
            if (IsProcessing || ActiveRenderer == null || Math.Abs(ActiveRenderer.BlackPoint - value) < 1e-6) return;
            
            // FIX BUG 1: Clamp rigido per evitare espansioni continue e slider instabili
            double safeValue = Math.Clamp(value, SliderMin, SliderMax);

            // Logica Pushing dinamica: se il nero supera il bianco, sposta il bianco in avanti nei limiti
            if (safeValue >= WhitePoint)
            {
                double delta = Math.Max(1e-4, Math.Abs(SliderMax - SliderMin) * 0.01);
                if (safeValue + delta > SliderMax) safeValue = SliderMax - delta;
                
                ActiveRenderer.WhitePoint = safeValue + delta;
                OnPropertyChanged(nameof(WhitePoint));
            }

            ActiveRenderer.BlackPoint = safeValue;
            OnPropertyChanged();
        }
    }

    public double WhitePoint
    {
        get => ActiveRenderer?.WhitePoint ?? 65535;
        set
        {
            if (IsProcessing || ActiveRenderer == null || Math.Abs(ActiveRenderer.WhitePoint - value) < 1e-6) return;

            double safeValue = Math.Clamp(value, SliderMin, SliderMax);

            // Logica Pushing dinamica: se il bianco scende sotto il nero, sposta il nero all'indietro
            if (safeValue <= BlackPoint)
            {
                double delta = Math.Max(1e-4, Math.Abs(SliderMax - SliderMin) * 0.01);
                if (safeValue - delta < SliderMin) safeValue = SliderMin + delta;

                ActiveRenderer.BlackPoint = safeValue - delta;
                OnPropertyChanged(nameof(BlackPoint));
            }

            ActiveRenderer.WhitePoint = safeValue;
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
    
    [ObservableProperty] 
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResetThresholdsCommand))] // FIX BUG 3: Notifica lo stato di abilitazione al Reset!
    private bool _isProcessing;
    
    [ObservableProperty] private string _statusText = string.Empty;

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
        _sourceFiles = files ?? throw new ArgumentNullException(nameof(files));
        _dataManager = dataManager ?? throw new ArgumentNullException(nameof(dataManager));
        _rendererFactory = rendererFactory ?? throw new ArgumentNullException(nameof(rendererFactory));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

        _statusText = LocalizationManager.Instance["StatusInit"];

        Navigator.UpdateStatus(0, _sourceFiles.Count);
        Navigator.IndexChanged += OnNavigatorIndexChanged;

        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        IsProcessing = true;
        try
        {
            if (_sourceFiles.Count > 0)
            {
                await LoadImageAtIndexAsync(0);
            }
            StatusText = LocalizationManager.Instance["StatusReady"];
        }
        catch (Exception ex)
        {
            StatusText = string.Format(LocalizationManager.Instance["ErrorInit"], ex.Message);
        }
        finally
        {
            IsProcessing = false;
        }
    }

    private void CalculateSliderBounds(FitsRenderer renderer)
    {
        if (renderer == null) return;
        
        double currentRange = Math.Abs(renderer.WhitePoint - renderer.BlackPoint);
        if (currentRange < 1e-4) currentRange = 1.0; // Fallback di sicurezza
        
        // Fissiamo lo slider attorno ai valori utili offrendo un margine stabile ai lati
        SliderMin = renderer.BlackPoint - (currentRange * 2.0);
        SliderMax = renderer.WhitePoint + (currentRange * 2.0);
    }

    // =======================================================================
    // 1. RENDERING PIPELINE (Flicker-Free & Adaptive)
    // =======================================================================

    private async void OnNavigatorIndexChanged(object? sender, int index)
    {
        OnPropertyChanged(nameof(CurrentImageText));
        await LoadImageAtIndexAsync(index);
    }

    private async Task LoadImageAtIndexAsync(int index)
    {
        if (index < 0 || index >= _sourceFiles.Count) return;

        _loadingCts?.Cancel();
        _loadingCts = new CancellationTokenSource();
        var token = _loadingCts.Token;

        try
        {
            StatusText = LocalizationManager.Instance["StatusLoading"];
            var fileRef = _sourceFiles[index];
            var data = await _dataManager.GetDataAsync(fileRef.FilePath);
            token.ThrowIfCancellationRequested();

            var imageHdu = data.FirstImageHdu ?? data.PrimaryHdu;
            if (imageHdu == null) 
                throw new InvalidOperationException(LocalizationManager.Instance["ErrorNoValidImageFound"]);

            var newRenderer = await _rendererFactory.CreateAsync(imageHdu.PixelData, fileRef.ModifiedHeader ?? imageHdu.Header);
            
            // --- GESTIONE SOGLIE E COERENZA VISIVA ---
            if (ActiveRenderer != null)
            {
                newRenderer.VisualizationMode = ActiveRenderer.VisualizationMode;

                if (AutoAdaptThresholds)
                {
                    newRenderer.ApplyRelativeProfile(ActiveRenderer.CaptureSigmaProfile());
                }
                else
                {
                    newRenderer.BlackPoint = ActiveRenderer.BlackPoint;
                    newRenderer.WhitePoint = ActiveRenderer.WhitePoint;
                }
            }
            else
            {
                // PRIMO AVVIO
                await newRenderer.ResetThresholdsAsync();
            }

            if (newRenderer.RenderBitDepth == FitsBitDepth.Float || 
                newRenderer.RenderBitDepth == FitsBitDepth.Double)
            {
                MaxLevels = 32;
                if (Levels > 32) Levels = 32;
            }
            else
            {
                MaxLevels = 64;
                if (!_hasLoadedFirstImage) Levels = 64; 
            }

            newRenderer.PostProcessAction = _coordinator.GetPreviewEffect(Levels);

            // FIX BUG 2: Calcoliamo e impostiamo i limiti PRIMA di assegnare ActiveRenderer.
            // Questo impedisce alla UI di Avalonia di tagliare valori appena applichiamo il binding.
            if (!_hasLoadedFirstImage)
            {
                CalculateSliderBounds(newRenderer);
            }

            var old = ActiveRenderer;
            ActiveRenderer = newRenderer;
            
            OnPropertyChanged(nameof(BlackPoint));
            OnPropertyChanged(nameof(WhitePoint));
            OnPropertyChanged(nameof(SelectedMode));

            Viewport.ImageSize = ActiveRenderer.ImageSize;
            
            if (!_hasLoadedFirstImage) 
            { 
                _hasLoadedFirstImage = true;
                await Task.Delay(50); 
                Viewport.ResetView(); 
            }

            StatusText = LocalizationManager.Instance["StatusReady"];
            old?.Dispose();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) 
        { 
            StatusText = string.Format(LocalizationManager.Instance["ErrorGeneric"], ex.Message); 
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
            CalculateSliderBounds(ActiveRenderer); // Ricalcola limiti fissi sul nuovo range
            OnPropertyChanged(nameof(BlackPoint));
            OnPropertyChanged(nameof(WhitePoint));
        }
    }

    private bool CanInteract() => ActiveRenderer != null && !IsProcessing;

    [RelayCommand(CanExecute = nameof(CanInteract))]
    private async Task Apply()
    {
        if (IsProcessing || ActiveRenderer == null) return;
        
        IsProcessing = true;
        try
        {
            var currentProfile = ActiveRenderer.CaptureSigmaProfile();

            var progress = new Progress<BatchProgressReport>(p => 
                StatusText = string.Format(LocalizationManager.Instance["StatusProcessingProgress"], p.CurrentFileIndex, p.TotalFiles));

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
            StatusText = string.Format(LocalizationManager.Instance["ErrorGeneric"], ex.Message);
        }
        finally
        {
            IsProcessing = false; 
        }
    }

    [RelayCommand] private void Cancel() => RequestClose?.Invoke();

    public void Dispose()
    {
        _loadingCts?.Cancel();
        _loadingCts?.Dispose();
        Navigator.IndexChanged -= OnNavigatorIndexChanged;
        ActiveRenderer?.Dispose();
        GC.SuppressFinalize(this);
    }
}