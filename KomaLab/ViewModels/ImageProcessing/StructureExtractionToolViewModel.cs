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

    // --- PARAMETRI SCIENTIFICI ---

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLarsonSekaninaVisible))]
    [NotifyPropertyChangedFor(nameof(IsUnsharpMaskingVisible))]
    [NotifyPropertyChangedFor(nameof(IsRvsfSingleVisible))]
    [NotifyPropertyChangedFor(nameof(IsRvsfMosaicVisible))]
    [NotifyPropertyChangedFor(nameof(DescriptionText))]
    private StructureExtractionMode _selectedMode = StructureExtractionMode.LarsonSekaninaStandard;

    // 1. Larson-Sekanina
    // Angolo aumentato a 3.0 (più visibile)
    [ObservableProperty] private double _rotationAngle = 3.0; 
    [ObservableProperty] private double _shiftX = 0.0;
    [ObservableProperty] private double _shiftY = 0.0;

    // 2. Unsharp Masking
    // Kernel aumentato a 25 (15 è spesso troppo rumoroso)
    [ObservableProperty] private double _kernelSize = 25.0;

    // 3. Adaptive RVSF
    // B ridotto a 0.2 (crescita più lenta e controllata)
    [ObservableProperty] private double _rvsfA_1 = 1.0;
    [ObservableProperty] private double _rvsfA_2 = 5.0; 

    [ObservableProperty] private double _rvsfB_1 = 0.2; 
    [ObservableProperty] private double _rvsfB_2 = 0.5; // Abbassato anche il max del mosaico

    [ObservableProperty] private double _rvsfN_1 = 1.0;
    [ObservableProperty] private double _rvsfN_2 = 0.5;

    [ObservableProperty] private bool _useLogScale = true;


    // --- PROPRIETÀ VISIBILITÀ UI ---

    public bool IsInteractionEnabled => !IsBusy && ActiveRenderer != null && CurrentState != StructureToolState.Processing;
    public bool IsSetupControlsEnabled => CurrentState == StructureToolState.Initial && !IsBusy;
    
    public bool IsCalculateButtonVisible => CurrentState == StructureToolState.Initial && !IsBusy;
    public bool IsApplyCancelButtonsVisible => CurrentState == StructureToolState.ResultsReady && !IsBusy;
    public bool IsProcessingVisible => IsBusy;

    public bool IsLarsonSekaninaVisible => 
        SelectedMode == StructureExtractionMode.LarsonSekaninaStandard || 
        SelectedMode == StructureExtractionMode.LarsonSekaninaSymmetric;

    public bool IsUnsharpMaskingVisible => 
        SelectedMode == StructureExtractionMode.UnsharpMaskingMedian;

    public bool IsRvsfSingleVisible => 
        SelectedMode == StructureExtractionMode.AdaptiveLaplacianRVSF;

    public bool IsRvsfMosaicVisible => 
        SelectedMode == StructureExtractionMode.AdaptiveLaplacianMosaic;

    public StructureExtractionMode[] AvailableModes => Enum.GetValues<StructureExtractionMode>();

    public string CurrentImageText => $"{Navigator.DisplayIndex} / {Navigator.TotalCount}";

    public string DescriptionText => SelectedMode switch
    {
        StructureExtractionMode.LarsonSekaninaStandard => 
            "Standard (Sottrattivo): Sottrae l'immagine ruotata (I - Rot). Ideale per evidenziare la morfologia a spirale dei getti.",
        StructureExtractionMode.LarsonSekaninaSymmetric => 
            "Simmetrico: (2*I - Rot(+) - Rot(-)). Riduce gli artefatti lineari e aumenta il contrasto, ma incrementa il rumore.",
        StructureExtractionMode.UnsharpMaskingMedian => 
            "Sottrae il background stimato tramite mediana locale. Utile per isolare rapidamente strutture ad alta frequenza.",
        StructureExtractionMode.AdaptiveLaplacianRVSF => 
            "Filtro RVSF adattivo: Raggio kernel variabile (A + B*ρ^N). Ottimizzato per strutture che si espandono radialmente.",
        StructureExtractionMode.AdaptiveLaplacianMosaic => 
            "Genera un mosaico 4x2 testando 8 combinazioni dei parametri RVSF (Min/Max per A, B, N). Utile per trovare i parametri ideali.",
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
        // Quando cambia l'indice, ricarichiamo l'immagine.
        // La logica interna di LoadImageAtIndexAsync deciderà se mostrare l'originale o l'anteprima processata.
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

            // --- CASO 1: ANTEPRIMA CALCOLATA (ResultsReady) ---
            // Se siamo in stato di anteprima, ogni volta che carichiamo un'immagine (anche navigando),
            // la processiamo immediatamente.
            if (CurrentState == StructureToolState.ResultsReady)
            {
                StatusText = "Elaborazione anteprima...";

                // 1. Calcolo dati reali (Restituisce Array: float[] o double[])
                var parameters = BuildParameters();
                
                // NOTA: Qui accettiamo 'Array' generico per supportare sia float[] che double[]
                Array processedPixels = await _coordinator.CalculatePreviewDataAsync(
                    fileRef, SelectedMode, parameters);

                // 2. Recupero header originale (per WCS e Metadata)
                var originalData = await _dataManager.GetDataAsync(fileRef.FilePath);
                var header = fileRef.ModifiedHeader ?? originalData.Header;

                // 3. Gestione Header Mosaico (Dimensioni cambiate)
                if (SelectedMode == StructureExtractionMode.AdaptiveLaplacianMosaic)
                {
                    header = header.Clone(); 
                    
                    int originalH = originalData.PixelData.GetLength(0); 
                    int originalW = originalData.PixelData.GetLength(1); 

                    int newW = originalW * 4;
                    int newH = originalH * 2;
                    
                    _metadataService.AddValue(header, "NAXIS1", newW, "Mosaic Width");
                    _metadataService.AddValue(header, "NAXIS2", newH, "Mosaic Height");
                }

                // 4. Creazione Renderer con i nuovi dati (passiamo Array generico)
                newRenderer = await _rendererFactory.CreateAsync(processedPixels, header);
                StatusText = "Anteprima attiva";
            }
            // --- CASO 2: IMMAGINE ORIGINALE (Initial) ---
            else
            {
                StatusText = "Caricamento originale...";
                var data = await _dataManager.GetDataAsync(fileRef.FilePath);
                newRenderer = await _rendererFactory.CreateAsync(data.PixelData, fileRef.ModifiedHeader ?? data.Header);
                StatusText = "Pronto";
            }

            token.ThrowIfCancellationRequested();

            // --- GESTIONE VISUALIZZAZIONE ---
            if (resetVisuals)
            {
                await newRenderer.ResetThresholdsAsync();
            }
            else if (ActiveRenderer != null)
            {
                // Se le dimensioni sono compatibili, mantieni lo stile di visualizzazione
                if (ActiveRenderer.ImageSize == newRenderer.ImageSize)
                {
                    var currentStyle = ActiveRenderer.CaptureSigmaProfile();
                    newRenderer.VisualizationMode = ActiveRenderer.VisualizationMode;
                    newRenderer.ApplyRelativeProfile(currentStyle);
                }
                else
                {
                    // Se le dimensioni cambiano (es. attivazione Mosaico), resetta lo zoom/stretch
                    await newRenderer.ResetThresholdsAsync();
                }
            }

            if (ActiveRenderer != null) ActiveRenderer.Dispose();
            ActiveRenderer = newRenderer;
            Viewport.ImageSize = ActiveRenderer.ImageSize;

            OnPropertyChanged(nameof(CurrentImageText));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) 
        { 
            StatusText = $"Errore: {ex.Message}";
            // Se fallisce l'anteprima, torniamo allo stato iniziale per sicurezza
            if (CurrentState == StructureToolState.ResultsReady)
            {
                CurrentState = StructureToolState.Initial;
            }
        }
        finally { IsBusy = false; }
    }

    // --- BUILD PARAMETERS ---
    
    private StructureExtractionParameters BuildParameters()
    {
        return new StructureExtractionParameters
        {
            // Larson-Sekanina
            RotationAngle = RotationAngle,
            ShiftX = ShiftX,
            ShiftY = ShiftY,
            
            // Unsharp
            KernelSize = (int)KernelSize,
            
            // RVSF Common
            UseLog = UseLogScale,
            
            // RVSF Values (Mapped from UI)
            ParamA_1 = RvsfA_1,
            ParamA_2 = SelectedMode == StructureExtractionMode.AdaptiveLaplacianMosaic ? RvsfA_2 : RvsfA_1,
            
            ParamB_1 = RvsfB_1,
            ParamB_2 = SelectedMode == StructureExtractionMode.AdaptiveLaplacianMosaic ? RvsfB_2 : RvsfB_1,
            
            ParamN_1 = RvsfN_1,
            ParamN_2 = SelectedMode == StructureExtractionMode.AdaptiveLaplacianMosaic ? RvsfN_2 : RvsfN_1,
        };
    }

    // --- COMANDI ---

    [RelayCommand(CanExecute = nameof(CanCalculate))]
    private async Task CalculatePreview()
    {
        if (ActiveRenderer == null) return;

        CurrentState = StructureToolState.Calculating;
        IsBusy = true;
        StatusText = "Elaborazione in corso...";

        try
        {
            // Cambiamo stato a ResultsReady PRIMA di caricare l'immagine.
            // LoadImageAtIndexAsync vedrà questo stato e applicherà il filtro.
            CurrentState = StructureToolState.ResultsReady;
            await LoadImageAtIndexAsync(Navigator.CurrentIndex, resetVisuals: true);
            StatusText = "Anteprima generata.";
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
                StatusText = $"Processando file {p.CurrentFileIndex} di {p.TotalFiles}...");

            var parameters = BuildParameters();

            var results = await _coordinator.ExecuteBatchAsync(
                _sourceFiles, SelectedMode, parameters, progress);

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
            // Se fallisce il batch, rimaniamo in modalità anteprima
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
            StatusText = "Ripristino originale...";
            try
            {
                CurrentState = StructureToolState.Initial;
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