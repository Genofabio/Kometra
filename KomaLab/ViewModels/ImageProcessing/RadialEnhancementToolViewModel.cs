using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KomaLab.Models.Fits;
using KomaLab.Models.Processing;
using KomaLab.Models.Processing.Enhancement;
using KomaLab.Services.Factories;
using KomaLab.Services.Fits;
using KomaLab.Services.Processing.Batch;
using KomaLab.Services.Processing.Coordinators;
using KomaLab.ViewModels.Visualization;
using SequenceNavigator = KomaLab.ViewModels.Shared.SequenceNavigator;

namespace KomaLab.ViewModels.ImageProcessing;

public enum RadialToolState
{
    Initial,        
    Calculating,    
    ResultsReady,   
    Processing      
}

public partial class RadialEnhancementToolViewModel : ObservableObject, IDisposable
{
    private readonly IFitsDataManager _dataManager;
    private readonly IFitsRendererFactory _rendererFactory;
    private readonly IRadialEnhancementCoordinator _coordinator;

    private readonly List<FitsFileReference> _sourceFiles;
    private CancellationTokenSource? _loadingCts;

    public TaskCompletionSource<bool> ImageLoadedTcs { get; } = new();
    public SequenceNavigator Navigator { get; } = new();
    public EnhancementImageViewport Viewport { get; } = new();

    public event Action? RequestClose;
    public bool DialogResult { get; private set; }
    public List<string> ResultPaths { get; private set; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInteractionEnabled))]
    private FitsRenderer? _activeRenderer;

    [ObservableProperty]
    // Notifica i comandi quando lo stato cambia per abilitare/disabilitare i bottoni
    [NotifyPropertyChangedFor(nameof(IsSetupControlsEnabled))]
    [NotifyPropertyChangedFor(nameof(IsCalculateButtonVisible))]
    [NotifyPropertyChangedFor(nameof(IsApplyCancelButtonsVisible))]
    [NotifyPropertyChangedFor(nameof(IsProcessingVisible))]
    [NotifyCanExecuteChangedFor(nameof(CalculatePreviewCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyBatchCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private RadialToolState _currentState = RadialToolState.Initial;

    [ObservableProperty]
    // Notifica i comandi quando siamo occupati
    [NotifyPropertyChangedFor(nameof(IsInteractionEnabled))]
    [NotifyPropertyChangedFor(nameof(IsSetupControlsEnabled))]
    [NotifyPropertyChangedFor(nameof(IsCalculateButtonVisible))]
    [NotifyPropertyChangedFor(nameof(IsApplyCancelButtonsVisible))]
    [NotifyPropertyChangedFor(nameof(IsProcessingVisible))]
    [NotifyCanExecuteChangedFor(nameof(CalculatePreviewCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyBatchCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isBusy;

    [ObservableProperty] private string _statusText = "Pronto";

    // --- Parametri Scientifici ---
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPolarParamsVisible))]
    [NotifyPropertyChangedFor(nameof(IsRenormParamsVisible))]
    [NotifyPropertyChangedFor(nameof(DescriptionText))]
    private RadialEnhancementMode _selectedMode = RadialEnhancementMode.InverseRho;

    // DEFINITI COME DOUBLE PER LA UI (Binding TextBox)
    [ObservableProperty] private double _radiusPixels = 500.0; 
    [ObservableProperty] private double _thetaPixels = 720.0;
    
    [ObservableProperty] private double _sigmaRejection = 3.0;
    [ObservableProperty] private double _contrastScale = 3.0;

    // --- Proprietà UI Derivate ---
    public bool IsInteractionEnabled => !IsBusy && ActiveRenderer != null && CurrentState != RadialToolState.Processing;
    public bool IsSetupControlsEnabled => CurrentState == RadialToolState.Initial && !IsBusy;
    
    public bool IsCalculateButtonVisible => CurrentState == RadialToolState.Initial && !IsBusy;
    public bool IsApplyCancelButtonsVisible => CurrentState == RadialToolState.ResultsReady && !IsBusy;
    public bool IsProcessingVisible => IsBusy;

    public bool IsPolarParamsVisible => SelectedMode != RadialEnhancementMode.InverseRho;
    public bool IsRenormParamsVisible => SelectedMode == RadialEnhancementMode.AzimuthalRenormalization;
    public RadialEnhancementMode[] AvailableModes => Enum.GetValues<RadialEnhancementMode>();

    public string CurrentImageText => $"{Navigator.DisplayIndex} / {Navigator.TotalCount}";

    public string DescriptionText => SelectedMode switch
    {
        RadialEnhancementMode.InverseRho => "Attenua la luminosità centrale (1/ρ) per evidenziare strutture a getto.",
        RadialEnhancementMode.AzimuthalAverage => "Sottrae la media azimutale, rimuovendo la chioma simmetrica.",
        RadialEnhancementMode.AzimuthalMedian => "Sottrae la mediana azimutale. Più robusto in campi stellari densi.",
        RadialEnhancementMode.AzimuthalRenormalization => "Normalizza il contrasto locale (Mclaughlin). Ottimo per dettagli deboli.",
        _ => ""
    };

    public RadialEnhancementToolViewModel(
        List<FitsFileReference> files,
        IFitsDataManager dataManager,
        IFitsRendererFactory rendererFactory,
        IRadialEnhancementCoordinator coordinator)
    {
        _sourceFiles = files;
        _dataManager = dataManager;
        _rendererFactory = rendererFactory;
        _coordinator = coordinator;

        Navigator.UpdateStatus(0, _sourceFiles.Count);
        Navigator.IndexChanged += async (_, idx) => await LoadImageAtIndexAsync(idx, resetVisuals: false);

        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            if (_sourceFiles.Count > 0)
            {
                await LoadImageAtIndexAsync(0, resetVisuals: true);

                // Auto-stima raggio ottimale (Min Width/Height / 2)
                if (ActiveRenderer != null)
                {
                    double w = ActiveRenderer.ImageSize.Width;
                    double h = ActiveRenderer.ImageSize.Height;
                    RadiusPixels = Math.Min(w, h) / 2.0;
                }
            }
                
            ImageLoadedTcs.TrySetResult(true);
        }
        catch (Exception ex)
        {
            StatusText = $"Errore inizializzazione: {ex.Message}";
            ImageLoadedTcs.TrySetException(ex);
        }
    }

    private async Task LoadImageAtIndexAsync(int index, bool resetVisuals)
    {
        IsBusy = true;
        _loadingCts?.Cancel();
        _loadingCts = new CancellationTokenSource();
        var token = _loadingCts.Token;
        
        try
        {
            StatusText = "Caricamento...";
            var fileRef = _sourceFiles[index];
            FitsRenderer newRenderer;

            if (CurrentState == RadialToolState.ResultsReady)
            {
                StatusText = "Elaborazione anteprima...";
                
                // ARROTONDAMENTO PER L'ENGINE (UI Double -> Engine Int)
                int nRadInt = (int)Math.Round(RadiusPixels);
                int nThetaInt = (int)Math.Round(ThetaPixels);

                var processedPixels = await _coordinator.CalculatePreviewDataAsync(
                    fileRef, SelectedMode, nRadInt, nThetaInt, SigmaRejection, ContrastScale);
                
                var header = await _coordinator.GetFileMetadataAsync(fileRef);
                newRenderer = await _rendererFactory.CreateAsync(processedPixels, header);
                StatusText = "Anteprima attiva";
            }
            else
            {
                var data = await _dataManager.GetDataAsync(fileRef.FilePath);
                token.ThrowIfCancellationRequested();
                newRenderer = await _rendererFactory.CreateAsync(data.PixelData, fileRef.ModifiedHeader ?? data.Header);
                StatusText = "Pronto";
            }

            token.ThrowIfCancellationRequested();

            if (resetVisuals)
            {
                await newRenderer.ResetThresholdsAsync();
            }
            else if (ActiveRenderer != null)
            {
                var currentStyle = ActiveRenderer.CaptureSigmaProfile();
                newRenderer.VisualizationMode = ActiveRenderer.VisualizationMode;
                newRenderer.ApplyRelativeProfile(currentStyle);
            }

            if (ActiveRenderer != null) ActiveRenderer.Dispose();
            ActiveRenderer = newRenderer;
            Viewport.ImageSize = ActiveRenderer.ImageSize;

            OnPropertyChanged(nameof(CurrentImageText));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusText = $"Errore Load: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    // --- COMANDI GENERATI (CommunityToolkit) ---

    // Genera: CalculatePreviewCommand
    [RelayCommand(CanExecute = nameof(CanCalculate))]
    private async Task CalculatePreview()
    {
        if (ActiveRenderer == null) return;

        CurrentState = RadialToolState.Calculating;
        IsBusy = true; 
        StatusText = "Avvio elaborazione...";

        try
        {
            CurrentState = RadialToolState.ResultsReady; 
            await LoadImageAtIndexAsync(Navigator.CurrentIndex, resetVisuals: true);
            StatusText = "Anteprima generata. Soglie resettate.";
        }
        catch (Exception ex)
        {
            StatusText = $"Errore Calcolo: {ex.Message}";
            CurrentState = RadialToolState.Initial;
            IsBusy = false; 
        }
    }

    // Metodo di controllo per CalculatePreviewCommand
    private bool CanCalculate() => !IsBusy && CurrentState == RadialToolState.Initial;

    // Genera: ApplyBatchCommand
    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyBatch()
    {
        CurrentState = RadialToolState.Processing;
        IsBusy = true;
        
        try
        {
            var progress = new Progress<BatchProgressReport>(p => 
                StatusText = $"Processando file {p.CurrentFileIndex} di {p.TotalFiles}...");

            // Arrotondamento per il Batch
            int nRadInt = (int)Math.Round(RadiusPixels);
            int nThetaInt = (int)Math.Round(ThetaPixels);

            var results = await _coordinator.ExecuteEnhancementAsync(
                _sourceFiles, SelectedMode, nRadInt, nThetaInt, SigmaRejection, ContrastScale, progress);

            if (results != null && results.Any())
            {
                ResultPaths = results;
                DialogResult = true;
                RequestClose?.Invoke();
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Errore Batch: {ex.Message}";
            CurrentState = RadialToolState.ResultsReady; 
        }
        finally { IsBusy = false; }
    }

    // Metodo di controllo per ApplyBatchCommand
    private bool CanApply() => !IsBusy && CurrentState == RadialToolState.ResultsReady;

    // Genera: CancelCommand
    [RelayCommand]
    private async Task Cancel()
    {
        if (CurrentState == RadialToolState.ResultsReady)
        {
            StatusText = "Ripristino originale...";
            try
            {
                CurrentState = RadialToolState.Initial; 
                await LoadImageAtIndexAsync(Navigator.CurrentIndex, resetVisuals: true);
                StatusText = "Pronto";
            }
            catch { IsBusy = false; }
        }
        else
        {
            DialogResult = false;
            RequestClose?.Invoke();
        }
    }

    // Genera: ResetThresholdsCommand
    [RelayCommand]
    private async Task ResetThresholds()
    {
        if (ActiveRenderer != null) await ActiveRenderer.ResetThresholdsAsync();
    }

    public void Dispose()
    {
        _loadingCts?.Cancel();
        ActiveRenderer?.Dispose();
    }
}