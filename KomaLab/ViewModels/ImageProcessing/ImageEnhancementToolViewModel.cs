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
using KomaLab.Services.Processing.Coordinators;
using KomaLab.ViewModels.Visualization;
using SequenceNavigator = KomaLab.ViewModels.Shared.SequenceNavigator;

namespace KomaLab.ViewModels.ImageProcessing;

public enum EnhancementToolState
{
    Initial,
    Calculating,
    ResultsReady,
    Processing
}

public partial class ImageEnhancementToolViewModel : ObservableObject, IDisposable
{
    // --- DIPENDENZE ---
    private readonly IFitsDataManager _dataManager;
    private readonly IFitsRendererFactory _rendererFactory;
    private readonly IImageEnhancementCoordinator _coordinator;
    private readonly IFitsMetadataService _metadataService;

    // --- DATI INTERNI ---
    private readonly List<FitsFileReference> _sourceFiles;
    private readonly EnhancementCategory _category;
    private CancellationTokenSource? _loadingCts;

    // --- EVENTI PER LA VIEW ---
    public event Action? RequestFitToScreen; 
    public event Action? RequestClose;

    // --- PROPRIETÀ PUBBLICHE ---
    public EnhancementCategory CurrentCategory => _category;
    public TaskCompletionSource<bool> ImageLoadedTcs { get; } = new();
    public SequenceNavigator Navigator { get; } = new();
    public EnhancementImageViewport Viewport { get; } = new();

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
    private EnhancementToolState _currentState = EnhancementToolState.Initial;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInteractionEnabled))]
    [NotifyPropertyChangedFor(nameof(IsSetupControlsEnabled))]
    [NotifyPropertyChangedFor(nameof(IsCalculateButtonVisible))]
    [NotifyPropertyChangedFor(nameof(IsApplyCancelButtonsVisible))]
    [NotifyPropertyChangedFor(nameof(IsProcessingVisible))]
    [NotifyCanExecuteChangedFor(nameof(CalculatePreviewCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyBatchCommand))]
    private bool _isBusy;

    [ObservableProperty] private string _statusText = "Pronto";
    [ObservableProperty] private double _progressValue;

    // --- MODALITÀ SELEZIONATA ---
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLarsonSekaninaVisible))]
    [NotifyPropertyChangedFor(nameof(IsRvsfMosaicVisible))]
    [NotifyPropertyChangedFor(nameof(IsRvsfSingleVisible))]
    [NotifyPropertyChangedFor(nameof(IsAzimuthalContainerVisible))] 
    [NotifyPropertyChangedFor(nameof(IsAngularQualityVisible))]    
    [NotifyPropertyChangedFor(nameof(IsMaskRadiusVisible))]        
    [NotifyPropertyChangedFor(nameof(IsRadialWeightedModelVisible))]
    [NotifyPropertyChangedFor(nameof(IsAzimuthalRejectionVisible))]
    [NotifyPropertyChangedFor(nameof(IsAzimuthalNormVisible))]
    [NotifyPropertyChangedFor(nameof(IsFrangiVisible))]
    [NotifyPropertyChangedFor(nameof(IsTensorVisible))]
    [NotifyPropertyChangedFor(nameof(IsTopHatVisible))]
    [NotifyPropertyChangedFor(nameof(IsClaheVisible))]
    [NotifyPropertyChangedFor(nameof(IsLocalNormVisible))]
    [NotifyPropertyChangedFor(nameof(IsKernelVisible))]
    [NotifyPropertyChangedFor(nameof(IsLaplaceVisible))] // <--- AGGIUNTO
    private ImageEnhancementMode _selectedMode;

    // --- PARAMETRI SCIENTIFICI ---
    [ObservableProperty] private double _rotationAngle = 5.0;
    [ObservableProperty] private double _shiftX = 0.0;
    [ObservableProperty] private double _shiftY = 0.0;
    [ObservableProperty] private bool _useLogScale = true;
    
    [ObservableProperty] private double _rvsfA_1 = 1.0;
    [ObservableProperty] private double _rvsfA_2 = 5.0; 
    [ObservableProperty] private double _rvsfB_1 = 0.2;
    [ObservableProperty] private double _rvsfB_2 = 0.5;
    [ObservableProperty] private double _rvsfN_1 = 1.0;
    [ObservableProperty] private double _rvsfN_2 = 0.5;
    
    [ObservableProperty] private int _radialSubsampling = 5;
    [ObservableProperty] private double _radialMaxRadius = 100.0;
    
    [ObservableProperty] private double _azimuthalRejSigma = 3.0;
    [ObservableProperty] private double _azimuthalNormSigma = 20.0;
    
    [ObservableProperty] private double _frangiSigma = 1.5;
    [ObservableProperty] private double _frangiBeta = 0.5;
    [ObservableProperty] private double _frangiC = 0.001;
    [ObservableProperty] private int _tensorSigma = 1;
    [ObservableProperty] private int _tensorRho = 3;
    [ObservableProperty] private int _topHatKernelSize = 21;
    
    [ObservableProperty] private int _kernelSize = 25; 
    [ObservableProperty] private double _claheClipLimit = 3.0;
    [ObservableProperty] private int _claheTileSize = 8;
    [ObservableProperty] private int _localNormWindowSize = 41;
    [ObservableProperty] private double _localNormIntensity = 1.0;

    public IEnumerable<ImageEnhancementMode> AvailableModes { get; }

    // --- PROPRIETÀ VISIBILITÀ ---
    public bool IsInteractionEnabled => !IsBusy && ActiveRenderer != null && CurrentState != EnhancementToolState.Processing;
    public bool IsSetupControlsEnabled => CurrentState == EnhancementToolState.Initial && !IsBusy;
    public bool IsCalculateButtonVisible => CurrentState == EnhancementToolState.Initial && !IsBusy;
    public bool IsApplyCancelButtonsVisible => (CurrentState == EnhancementToolState.ResultsReady || CurrentState == EnhancementToolState.Processing) && !IsBusy;
    public bool IsProcessingVisible => IsBusy;
    public string CurrentImageText => $"{Navigator.DisplayIndex} / {Navigator.TotalCount}";
    
    public bool IsLarsonSekaninaVisible => SelectedMode is ImageEnhancementMode.LarsonSekaninaStandard or ImageEnhancementMode.LarsonSekaninaSymmetric;
    public bool IsRvsfSingleVisible => SelectedMode == ImageEnhancementMode.AdaptiveLaplacianRVSF;
    public bool IsRvsfMosaicVisible => SelectedMode == ImageEnhancementMode.AdaptiveLaplacianMosaic;
    
    // FIX: Rimosso InverseRho e AzimuthalMedian (che non hanno parametri)
    // Così il contenitore (e il titolo) spariscono quando non ci sono controlli.
    public bool IsAzimuthalContainerVisible => SelectedMode is ImageEnhancementMode.AzimuthalAverage 
                                                   or ImageEnhancementMode.AzimuthalRenormalization 
                                                   // or ImageEnhancementMode.InverseRho  <-- RIMOSSO
                                                   // or ImageEnhancementMode.AzimuthalMedian <-- RIMOSSO
                                                   or ImageEnhancementMode.MedianComaModel
                                                   or ImageEnhancementMode.RadialWeightedModel;

    public bool IsAngularQualityVisible => SelectedMode == ImageEnhancementMode.MedianComaModel;

    public bool IsMaskRadiusVisible => SelectedMode == ImageEnhancementMode.MedianComaModel 
                                    || SelectedMode == ImageEnhancementMode.RadialWeightedModel;

    public bool IsRadialWeightedModelVisible => false; 

    public bool IsAzimuthalRejectionVisible => SelectedMode is ImageEnhancementMode.AzimuthalAverage 
                                                            or ImageEnhancementMode.AzimuthalRenormalization;

    public bool IsAzimuthalNormVisible => SelectedMode is ImageEnhancementMode.AzimuthalRenormalization;

    public bool IsFrangiVisible => SelectedMode == ImageEnhancementMode.FrangiVesselnessFilter;
    public bool IsTensorVisible => SelectedMode == ImageEnhancementMode.StructureTensorCoherence;
    public bool IsTopHatVisible => SelectedMode == ImageEnhancementMode.WhiteTopHatExtraction;
    public bool IsKernelVisible => SelectedMode == ImageEnhancementMode.UnsharpMaskingMedian;
    public bool IsClaheVisible => SelectedMode == ImageEnhancementMode.ClaheLocalContrast;
    public bool IsLocalNormVisible => SelectedMode == ImageEnhancementMode.AdaptiveLocalNormalization;
    
    // <--- AGGIUNTO
    public bool IsLaplaceVisible => SelectedMode == ImageEnhancementMode.AdaptiveLaplaceFilter;

    public ImageEnhancementToolViewModel(
        EnhancementCategory targetCategory,
        List<FitsFileReference> files,
        IFitsDataManager dataManager,
        IFitsRendererFactory rendererFactory,
        IImageEnhancementCoordinator coordinator,
        IFitsMetadataService metadataService)
    {
        _category = targetCategory;
        _sourceFiles = files;
        _dataManager = dataManager;
        _rendererFactory = rendererFactory;
        _coordinator = coordinator;
        _metadataService = metadataService;

        AvailableModes = Enum.GetValues<ImageEnhancementMode>()
                             .Cast<ImageEnhancementMode>()
                             .Where(m => m.GetCategory() == _category)
                             .ToList();

        if (AvailableModes.Any()) SelectedMode = AvailableModes.First();

        Navigator.UpdateStatus(0, _sourceFiles.Count);
        
        Navigator.IndexChanged += async (_, idx) => await LoadImageAtIndexAsync(idx, resetVisuals: false, autoFit: false);

        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            if (_sourceFiles.Count > 0) await LoadImageAtIndexAsync(0, resetVisuals: true, autoFit: true);
            ImageLoadedTcs.TrySetResult(true);
        }
        catch (Exception ex)
        {
            StatusText = $"Errore: {ex.Message}";
            ImageLoadedTcs.TrySetException(ex);
        }
    }

    private async Task LoadImageAtIndexAsync(int index, bool resetVisuals, bool autoFit = false)
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

            if (CurrentState == EnhancementToolState.ResultsReady)
            {
                StatusText = "Elaborazione anteprima...";
                var parameters = BuildParameters();
                
                Array processedPixels = await _coordinator.CalculatePreviewDataAsync(fileRef, SelectedMode, parameters, token);
                token.ThrowIfCancellationRequested();

                var originalData = await _dataManager.GetDataAsync(fileRef.FilePath);
                var originalHdu = originalData.FirstImageHdu ?? originalData.PrimaryHdu;
                if (originalHdu == null) throw new InvalidOperationException("File sorgente privo di immagini.");

                var header = fileRef.ModifiedHeader ?? originalHdu.Header;

                if (SelectedMode == ImageEnhancementMode.AdaptiveLaplacianMosaic)
                {
                    header = header.Clone();
                    _metadataService.AddValue(header, "NAXIS1", originalHdu.PixelData.GetLength(1) * 4, "Mosaic Width");
                    _metadataService.AddValue(header, "NAXIS2", originalHdu.PixelData.GetLength(0) * 2, "Mosaic Height");
                }

                newRenderer = await _rendererFactory.CreateAsync(processedPixels, header);
                StatusText = "Anteprima attiva";
            }
            else
            {
                var data = await _dataManager.GetDataAsync(fileRef.FilePath);
                token.ThrowIfCancellationRequested();
                
                var imageHdu = data.FirstImageHdu ?? data.PrimaryHdu;
                if (imageHdu == null) throw new InvalidOperationException("Nessuna immagine valida trovata.");

                newRenderer = await _rendererFactory.CreateAsync(imageHdu.PixelData, fileRef.ModifiedHeader ?? imageHdu.Header);
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
                else 
                {
                    await newRenderer.ResetThresholdsAsync();
                }
            }

            ActiveRenderer?.Dispose();
            ActiveRenderer = newRenderer;
            Viewport.ImageSize = ActiveRenderer.ImageSize;
            OnPropertyChanged(nameof(CurrentImageText));

            if (autoFit)
            {
                RequestFitToScreen?.Invoke();
            }
        }
        catch (OperationCanceledException) 
        { 
            StatusText = "Caricamento interrotto";
        }
        catch (Exception ex) 
        { 
            StatusText = $"Errore: {ex.Message}";
            if (CurrentState == EnhancementToolState.ResultsReady) CurrentState = EnhancementToolState.Initial;
        }
        finally { IsBusy = false; ProgressValue = 0; }
    }

    private ImageEnhancementParameters BuildParameters()
    {
        return new ImageEnhancementParameters
        {
            RotationAngle = RotationAngle, ShiftX = ShiftX, ShiftY = ShiftY,
            UseLog = UseLogScale,
            ParamA_1 = RvsfA_1, ParamA_2 = RvsfA_2,
            ParamB_1 = RvsfB_1, ParamB_2 = RvsfB_2,
            ParamN_1 = RvsfN_1, ParamN_2 = RvsfN_2,
            RadialSubsampling = RadialSubsampling,
            RadialMaxRadius = RadialMaxRadius,
            AzimuthalRejSigma = AzimuthalRejSigma, AzimuthalNormSigma = AzimuthalNormSigma,
            FrangiSigma = FrangiSigma, FrangiBeta = FrangiBeta, FrangiC = FrangiC,
            TensorSigma = TensorSigma, TensorRho = TensorRho, TopHatKernelSize = TopHatKernelSize,
            KernelSize = KernelSize,
            ClaheClipLimit = ClaheClipLimit, ClaheTileSize = ClaheTileSize,
            LocalNormWindowSize = LocalNormWindowSize, LocalNormIntensity = LocalNormIntensity
        };
    }

    [RelayCommand(CanExecute = nameof(CanCalculate))]
    private async Task CalculatePreview()
    {
        if (ActiveRenderer == null) return;
        IsBusy = true;
        try
        {
            CurrentState = EnhancementToolState.ResultsReady;
            await LoadImageAtIndexAsync(Navigator.CurrentIndex, resetVisuals: true, autoFit: true);
        }
        catch (Exception ex)
        {
            StatusText = $"Errore Calcolo: {ex.Message}";
            CurrentState = EnhancementToolState.Initial;
        }
        finally { IsBusy = false; }
    }
    private bool CanCalculate() => !IsBusy && CurrentState == EnhancementToolState.Initial;

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyBatch()
    {
        CurrentState = EnhancementToolState.Processing;
        IsBusy = true;

        _loadingCts?.Cancel();
        _loadingCts = new CancellationTokenSource();
        var token = _loadingCts.Token;

        try
        {
            var progress = new Progress<BatchProgressReport>(p =>
                StatusText = $"File {p.CurrentFileIndex} / {p.TotalFiles}...");

            var results = await _coordinator.ExecuteBatchAsync(
                _sourceFiles, 
                SelectedMode, 
                BuildParameters(), 
                progress, 
                token);

            if (results?.Any() == true)
            {
                ResultPaths = results;
                DialogResult = true;
                RequestClose?.Invoke();
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "Elaborazione batch annullata.";
            CurrentState = EnhancementToolState.ResultsReady;
        }
        catch (Exception ex)
        {
            StatusText = $"Errore Batch: {ex.Message}";
            CurrentState = EnhancementToolState.ResultsReady;
        }
        finally { IsBusy = false; }
    }
    private bool CanApply() => !IsBusy && CurrentState == EnhancementToolState.ResultsReady;

    [RelayCommand]
    private async Task Cancel()
    {
        _loadingCts?.Cancel();

        if (CurrentState == EnhancementToolState.ResultsReady)
        {
            CurrentState = EnhancementToolState.Initial;
            await LoadImageAtIndexAsync(Navigator.CurrentIndex, resetVisuals: true, autoFit: false);
        }
        else if (CurrentState == EnhancementToolState.Processing)
        {
            CurrentState = EnhancementToolState.ResultsReady;
            StatusText = "Operazione interrotta";
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
        if (_loadingCts != null)
        {
            try { _loadingCts.Cancel(); } catch (ObjectDisposedException) { }
            finally { _loadingCts.Dispose(); _loadingCts = null; }
        }
        var rendererToDispose = ActiveRenderer;
        ActiveRenderer = null; 
        rendererToDispose?.Dispose();
    }
}