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
using KomaLab.Services.Fits.Metadata;
using KomaLab.Services.Processing.Batch;
using KomaLab.Services.Processing.Coordinators;
using KomaLab.ViewModels.Visualization;
using SequenceNavigator = KomaLab.ViewModels.Shared.SequenceNavigator;

namespace KomaLab.ViewModels.ImageProcessing;

public enum StructureToolState
{
    Initial,
    Calculating,
    ResultsReady,
    Processing
}

public partial class StructureExtractionToolViewModel : ObservableObject, IDisposable
{
    private readonly IFitsDataManager _dataManager;
    private readonly IFitsRendererFactory _rendererFactory;
    private readonly IStructureExtractionCoordinator _coordinator;
    private readonly IFitsMetadataService _metadataService; 

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

    // --- GESTIONE STATO ---
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSetupControlsEnabled))]
    [NotifyPropertyChangedFor(nameof(IsCalculateButtonVisible))]
    [NotifyPropertyChangedFor(nameof(IsApplyCancelButtonsVisible))]
    [NotifyPropertyChangedFor(nameof(IsProcessingVisible))]
    [NotifyCanExecuteChangedFor(nameof(CalculatePreviewCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyBatchCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private StructureToolState _currentState = StructureToolState.Initial;

    [ObservableProperty]
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
    [ObservableProperty] private double _progressValue;

    // --- PARAMETRI SCIENTIFICI ---

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLarsonSekaninaVisible))]
    [NotifyPropertyChangedFor(nameof(IsUnsharpMaskingVisible))]
    [NotifyPropertyChangedFor(nameof(IsRvsfSingleVisible))]
    [NotifyPropertyChangedFor(nameof(IsRvsfMosaicVisible))]
    [NotifyPropertyChangedFor(nameof(IsFrangiVisible))]
    [NotifyPropertyChangedFor(nameof(IsStructureTensorVisible))]
    [NotifyPropertyChangedFor(nameof(IsTopHatVisible))]
    [NotifyPropertyChangedFor(nameof(IsClaheVisible))]
    [NotifyPropertyChangedFor(nameof(IsLocalNormVisible))]
    [NotifyPropertyChangedFor(nameof(DescriptionText))]
    private StructureExtractionMode _selectedMode = StructureExtractionMode.LarsonSekaninaStandard;

    // 1. Larson-Sekanina
    [ObservableProperty] private double _rotationAngle = 3.0; 
    [ObservableProperty] private double _shiftX = 0.0;
    [ObservableProperty] private double _shiftY = 0.0;

    // 2. Unsharp Masking
    [ObservableProperty] private double _kernelSize = 25.0;

    // 3. Adaptive RVSF
    [ObservableProperty] private double _rvsfA_1 = 1.0;
    [ObservableProperty] private double _rvsfA_2 = 5.0; 
    [ObservableProperty] private double _rvsfB_1 = 0.2; 
    [ObservableProperty] private double _rvsfB_2 = 0.5; 
    [ObservableProperty] private double _rvsfN_1 = 1.0;
    [ObservableProperty] private double _rvsfN_2 = 0.5;
    [ObservableProperty] private bool _useLogScale = true;

    // 4. Frangi Vesselness
    [ObservableProperty] private double _frangiSigma = 1.5;
    [ObservableProperty] private double _frangiBeta = 0.5;
    [ObservableProperty] private double _frangiC = 0.001;

    // 5. Structure Tensor
    [ObservableProperty] private int _tensorSigma = 1;
    [ObservableProperty] private int _tensorRho = 3;

    // 6. White Top-Hat
    [ObservableProperty] private int _topHatKernelSize = 21;

    // 7. CLAHE
    [ObservableProperty] private double _claheClipLimit = 3.0;
    [ObservableProperty] private int _claheTileSize = 8;

    // 8. Local Statistical Normalization (LSN)
    [ObservableProperty] private int _localNormWindowSize = 41;
    [ObservableProperty] private double _localNormIntensity = 1.0;


    // --- PROPRIETÀ VISIBILITÀ UI ---

    public bool IsInteractionEnabled => !IsBusy && ActiveRenderer != null && CurrentState != StructureToolState.Processing;
    public bool IsSetupControlsEnabled => CurrentState == StructureToolState.Initial && !IsBusy;
    
    public bool IsCalculateButtonVisible => CurrentState == StructureToolState.Initial && !IsBusy;
    public bool IsApplyCancelButtonsVisible => CurrentState == StructureToolState.ResultsReady && !IsBusy;
    public bool IsProcessingVisible => IsBusy;

    public bool IsLarsonSekaninaVisible => SelectedMode is StructureExtractionMode.LarsonSekaninaStandard or StructureExtractionMode.LarsonSekaninaSymmetric;
    public bool IsUnsharpMaskingVisible => SelectedMode == StructureExtractionMode.UnsharpMaskingMedian;
    public bool IsRvsfSingleVisible => SelectedMode == StructureExtractionMode.AdaptiveLaplacianRVSF;
    public bool IsRvsfMosaicVisible => SelectedMode == StructureExtractionMode.AdaptiveLaplacianMosaic;
    public bool IsFrangiVisible => SelectedMode == StructureExtractionMode.FrangiVesselnessFilter;
    public bool IsStructureTensorVisible => SelectedMode == StructureExtractionMode.StructureTensorCoherence;
    public bool IsTopHatVisible => SelectedMode == StructureExtractionMode.WhiteTopHatExtraction;
    public bool IsClaheVisible => SelectedMode == StructureExtractionMode.ClaheLocalContrast;
    public bool IsLocalNormVisible => SelectedMode == StructureExtractionMode.AdaptiveLocalNormalization;

    public StructureExtractionMode[] AvailableModes => Enum.GetValues<StructureExtractionMode>();

    public string CurrentImageText => $"{Navigator.DisplayIndex} / {Navigator.TotalCount}";

    public string DescriptionText => SelectedMode switch
    {
        StructureExtractionMode.LarsonSekaninaStandard => "Sottrae l'immagine ruotata ($I - Rot$). Ideale per spirali e getti curvi.",
        StructureExtractionMode.LarsonSekaninaSymmetric => "Sottrae rotazioni opposte ($2I - Rot+ - Rot-$). Più contrasto, meno artefatti.",
        StructureExtractionMode.UnsharpMaskingMedian => "Sottrae il fondo stimato via mediana locale. Isola rapidamente dettagli fini.",
        StructureExtractionMode.AdaptiveLaplacianRVSF => "Filtro radiale adattivo ($A + B \\cdot \\rho^N$). Esalta scie ed espansioni dal nucleo.",
        StructureExtractionMode.AdaptiveLaplacianMosaic => "Testa 8 combinazioni RVSF. Ideale per la ricerca empirica dei parametri.",
        StructureExtractionMode.FrangiVesselnessFilter => "Analisi multiscala dell'Hessiana. Isola getti filamentosi e curvi sopprimendo stelle.",
        StructureExtractionMode.StructureTensorCoherence => "Analisi anisotropia locale. Isola i flussi direzionali e i getti rettilinei.",
        StructureExtractionMode.WhiteTopHatExtraction => "Estrae dettagli luminosi più piccoli del kernel. Ottimo per 'pulire' il bagliore del nucleo.",
        StructureExtractionMode.ClaheLocalContrast => "Equalizzazione adattiva (16-bit). Esalta il contrasto locale in zone HDR (chioma vs nucleo).",
        StructureExtractionMode.AdaptiveLocalNormalization => "Normalizzazione statistica ($z-score$). 100% Float-safe, preserva l'integrità del dato FITS.",
        _ => ""
    };

    public StructureExtractionToolViewModel(
        List<FitsFileReference> files,
        IFitsDataManager dataManager,
        IFitsRendererFactory rendererFactory,
        IStructureExtractionCoordinator coordinator,
        IFitsMetadataService metadataService)
    {
        _sourceFiles = files;
        _dataManager = dataManager;
        _rendererFactory = rendererFactory;
        _coordinator = coordinator;
        _metadataService = metadataService;

        Navigator.UpdateStatus(0, _sourceFiles.Count);
        Navigator.IndexChanged += async (_, idx) => await LoadImageAtIndexAsync(idx, resetVisuals: false);

        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            if (_sourceFiles.Count > 0) await LoadImageAtIndexAsync(0, resetVisuals: true);
            ImageLoadedTcs.TrySetResult(true);
        }
        catch (Exception ex)
        {
            StatusText = $"Errore: {ex.Message}";
            ImageLoadedTcs.TrySetException(ex);
        }
    }

    private async Task LoadImageAtIndexAsync(int index, bool resetVisuals)
    {
        IsBusy = true;
        ProgressValue = 0;
        _loadingCts?.Cancel();
        _loadingCts = new CancellationTokenSource();
        var token = _loadingCts.Token;

        try
        {
            StatusText = "Caricamento...";
            var fileRef = _sourceFiles[index];
            FitsRenderer newRenderer;

            if (CurrentState == StructureToolState.ResultsReady)
            {
                StatusText = "Elaborazione anteprima...";
                var parameters = BuildParameters();
                var progress = new Progress<double>(p => ProgressValue = p);

                // Calcolo asincrono tramite coordinator
                Array processedPixels = await _coordinator.CalculatePreviewDataAsync(fileRef, SelectedMode, parameters);

                var originalData = await _dataManager.GetDataAsync(fileRef.FilePath);
                var header = fileRef.ModifiedHeader ?? originalData.Header;

                if (SelectedMode == StructureExtractionMode.AdaptiveLaplacianMosaic)
                {
                    header = header.Clone();
                    _metadataService.AddValue(header, "NAXIS1", originalData.PixelData.GetLength(1) * 4, "Mosaic Width");
                    _metadataService.AddValue(header, "NAXIS2", originalData.PixelData.GetLength(0) * 2, "Mosaic Height");
                }

                newRenderer = await _rendererFactory.CreateAsync(processedPixels, header);
                StatusText = "Anteprima attiva";
            }
            else
            {
                var data = await _dataManager.GetDataAsync(fileRef.FilePath);
                newRenderer = await _rendererFactory.CreateAsync(data.PixelData, fileRef.ModifiedHeader ?? data.Header);
                StatusText = "Pronto";
            }

            token.ThrowIfCancellationRequested();

            if (resetVisuals) await newRenderer.ResetThresholdsAsync();
            else if (ActiveRenderer != null)
            {
                if (ActiveRenderer.ImageSize == newRenderer.ImageSize)
                {
                    var profile = ActiveRenderer.CaptureSigmaProfile();
                    newRenderer.VisualizationMode = ActiveRenderer.VisualizationMode;
                    newRenderer.ApplyRelativeProfile(profile);
                }
                else await newRenderer.ResetThresholdsAsync();
            }

            ActiveRenderer?.Dispose();
            ActiveRenderer = newRenderer;
            Viewport.ImageSize = ActiveRenderer.ImageSize;
            OnPropertyChanged(nameof(CurrentImageText));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) 
        { 
            StatusText = $"Errore: {ex.Message}";
            if (CurrentState == StructureToolState.ResultsReady) CurrentState = StructureToolState.Initial;
        }
        finally { IsBusy = false; ProgressValue = 0; }
    }

    private StructureExtractionParameters BuildParameters()
    {
        return new StructureExtractionParameters
        {
            RotationAngle = RotationAngle,
            ShiftX = ShiftX,
            ShiftY = ShiftY,
            KernelSize = (int)KernelSize,
            UseLog = UseLogScale,
            ParamA_1 = RvsfA_1,
            ParamA_2 = SelectedMode == StructureExtractionMode.AdaptiveLaplacianMosaic ? RvsfA_2 : RvsfA_1,
            ParamB_1 = RvsfB_1,
            ParamB_2 = SelectedMode == StructureExtractionMode.AdaptiveLaplacianMosaic ? RvsfB_2 : RvsfB_1,
            ParamN_1 = RvsfN_1,
            ParamN_2 = SelectedMode == StructureExtractionMode.AdaptiveLaplacianMosaic ? RvsfN_2 : RvsfN_1,
            
            // Nuovi parametri scientifici
            FrangiSigma = FrangiSigma,
            FrangiBeta = FrangiBeta,
            FrangiC = FrangiC,
            TensorSigma = TensorSigma,
            TensorRho = TensorRho,
            TopHatKernelSize = TopHatKernelSize,
            ClaheClipLimit = ClaheClipLimit,
            ClaheTileSize = ClaheTileSize,
            LocalNormWindowSize = LocalNormWindowSize,
            LocalNormIntensity = LocalNormIntensity
        };
    }

    [RelayCommand(CanExecute = nameof(CanCalculate))]
    private async Task CalculatePreview()
    {
        if (ActiveRenderer == null) return;
        IsBusy = true;
        try
        {
            CurrentState = StructureToolState.ResultsReady;
            await LoadImageAtIndexAsync(Navigator.CurrentIndex, resetVisuals: true);
        }
        catch (Exception ex)
        {
            StatusText = $"Errore Calcolo: {ex.Message}";
            CurrentState = StructureToolState.Initial;
        }
        finally { IsBusy = false; }
    }
    private bool CanCalculate() => !IsBusy && CurrentState == StructureToolState.Initial;

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyBatch()
    {
        CurrentState = StructureToolState.Processing;
        IsBusy = true;
        try
        {
            var progress = new Progress<BatchProgressReport>(p =>
                StatusText = $"File {p.CurrentFileIndex} / {p.TotalFiles}...");

            var results = await _coordinator.ExecuteBatchAsync(_sourceFiles, SelectedMode, BuildParameters(), progress);

            if (results?.Any() == true)
            {
                ResultPaths = results;
                DialogResult = true;
                RequestClose?.Invoke();
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Errore Batch: {ex.Message}";
            CurrentState = StructureToolState.ResultsReady;
        }
        finally { IsBusy = false; }
    }
    private bool CanApply() => !IsBusy && CurrentState == StructureToolState.ResultsReady;

    [RelayCommand]
    private async Task Cancel()
    {
        if (CurrentState == StructureToolState.ResultsReady)
        {
            CurrentState = StructureToolState.Initial;
            await LoadImageAtIndexAsync(Navigator.CurrentIndex, resetVisuals: true);
        }
        else
        {
            DialogResult = false;
            RequestClose?.Invoke();
        }
    }

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