using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Diagnostics;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using Avalonia.Media;     
using Avalonia.Media.Imaging;
using KomaLab.Models;
using KomaLab.Models.Primitives;
using KomaLab.Models.Processing;
using KomaLab.Models.Visualization;
using KomaLab.Services.Astrometry;
using KomaLab.Services.Data;
using KomaLab.Services.Imaging;
using KomaLab.ViewModels.Helpers;
using CoordinateEntry = KomaLab.ViewModels.Helpers.CoordinateEntry;
using Point = Avalonia.Point;
using Size = Avalonia.Size; 

namespace KomaLab.ViewModels;

public partial class AlignmentToolViewModel : ObservableObject, IDisposable
{
    #region Campi e Costanti
    
    // --- Dipendenze Enterprise ---
    private readonly IFitsIoService _ioService;           // Sostituisce _fitsService
    private readonly IFitsMetadataService _metadataService; // Nuovo servizio Metadati
    private readonly IAlignmentService _alignmentService;
    private readonly IFitsImageDataConverter _converter;
    private readonly IImageAnalysisService _analysis;
    private readonly IJplHorizonsService _jplService;
    
    // --- Dati Interni ---
    private readonly List<string> _sourcePaths; 
    private int _currentStackIndex;
    private readonly int _totalStackCount;
    
    private Size _viewportSize;
    private ContrastProfile? _lastContrastProfile;
    
    // Flag validazione Astrometrica (Stelle/JPL)
    private bool _isAstrometryValid = false;

    // --- Risorse Grafiche Statiche ---
    private static readonly IBrush ColorNormal = Brushes.Cyan;
    private static readonly IBrush ColorSuccess = new SolidColorBrush(Color.Parse("#03A077")); 
    private static readonly IBrush ColorError = new SolidColorBrush(Color.Parse("#E6606A"));   
    private static readonly IBrush ColorLoading = new SolidColorBrush(Color.Parse("#8058E8")); 

    #endregion

    #region Proprietà
    
    public List<string>? FinalProcessedPaths { get; private set; }
    public AlignmentImageViewport Viewport { get; } = new();
    public ObservableCollection<CoordinateEntry> CoordinateEntries { get; } = new();

    public Size ViewportSize
    {
        get => _viewportSize;
        set { _viewportSize = value; Viewport.ViewportSize = value; }
    }
    
    // --- Gestione Immagine Attiva ---
    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(CorrectImageSize))]
    [NotifyPropertyChangedFor(nameof(SafeImage))]       
    [NotifyPropertyChangedFor(nameof(SafeImageWidth))]  
    [NotifyPropertyChangedFor(nameof(SafeImageHeight))] 
    private FitsRenderer? _activeImage; 
    
    public Size CorrectImageSize => ActiveImage?.ImageSize ?? default;
    public Bitmap? SafeImage => ActiveImage?.Image;
    public double SafeImageWidth => ActiveImage?.ImageSize.Width ?? 100;
    public double SafeImageHeight => ActiveImage?.ImageSize.Height ?? 100;

    // --- Radiometria ---
    [ObservableProperty] private double _blackPoint;
    [ObservableProperty] private double _whitePoint;
    
    // --- Stato Visualizzazione (Linear, Log, etc.) ---
    [ObservableProperty]
    private VisualizationMode _visualizationMode = VisualizationMode.Linear;

    // Trigger immediato quando l'utente cambia modo dalla UI
    partial void OnVisualizationModeChanged(VisualizationMode value)
    {
        if (ActiveImage != null) ActiveImage.VisualizationMode = value;
    }

    // --- UI Info ---
    public string ZoomStatusText => $"{Viewport.Scale:P0}";
    public bool IsTargetMarkerVisible => TargetCoordinate.HasValue;
    
    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(IsTargetMarkerVisible))]
    [NotifyPropertyChangedFor(nameof(IsSearchBoxVisible))]
    private Point? _targetCoordinate;

    [ObservableProperty] private bool _isStack;
    [ObservableProperty] private string _stackCounterText = "";
    
    // --- Selezione Target e Modalità ---
    public IEnumerable<AlignmentTarget> AvailableTargets => Enum.GetValues<AlignmentTarget>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AvailableAlignmentModes))]
    [NotifyPropertyChangedFor(nameof(IsSearchRadiusVisible))]
    [NotifyPropertyChangedFor(nameof(IsSearchRadiusControlsVisible))]
    [NotifyPropertyChangedFor(nameof(IsRefinementMessageVisible))]
    [NotifyPropertyChangedFor(nameof(IsNavigationVisible))]
    [NotifyPropertyChangedFor(nameof(IsJplOptionVisible))]
    [NotifyPropertyChangedFor(nameof(AstrometryOptionLabel))]      
    [NotifyPropertyChangedFor(nameof(IsTargetNameInputVisible))]   
    [NotifyPropertyChangedFor(nameof(IsVerifyButtonVisible))] 
    private AlignmentTarget _selectedTarget = AlignmentTarget.Comet;

    public IEnumerable<AlignmentMode> AvailableAlignmentModes 
    {
        get
        {
            if (SelectedTarget == AlignmentTarget.Comet)
            {
                if (IsStack) return new[] { AlignmentMode.Automatic, AlignmentMode.Guided, AlignmentMode.Manual };
                return new[] { AlignmentMode.Automatic, AlignmentMode.Manual };
            }
            // Per le stelle supportiamo solo Automatico (Pattern Matching)
            return new[] { AlignmentMode.Automatic };
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSearchRadiusVisible))]
    [NotifyPropertyChangedFor(nameof(IsSearchRadiusControlsVisible))]
    [NotifyPropertyChangedFor(nameof(IsRefinementMessageVisible))]
    [NotifyPropertyChangedFor(nameof(IsNavigationVisible))]
    [NotifyPropertyChangedFor(nameof(IsJplOptionVisible))] 
    private AlignmentMode _selectedMode = AlignmentMode.Automatic;
    
    // --- Parametri di Ricerca ---
    public bool IsSearchRadiusVisible => SelectedTarget == AlignmentTarget.Comet && SelectedMode != AlignmentMode.Automatic;

    [ObservableProperty] private int _minSearchRadius;
    [ObservableProperty] private int _maxSearchRadius = 100;
    [ObservableProperty] private int _searchRadius = 100;

    // --- Logica Astrometria / JPL ---
    public bool IsJplOptionVisible => 
        CurrentState == AlignmentState.Initial &&
        (
            (SelectedTarget == AlignmentTarget.Comet && SelectedMode != AlignmentMode.Manual) || 
            (SelectedTarget == AlignmentTarget.Stars) 
        );

    public string AstrometryOptionLabel => SelectedTarget == AlignmentTarget.Stars 
        ? "Usa allineamento WCS (Header)" 
        : "Usa riferimento NASA/JPL";

    public bool IsTargetNameInputVisible => SelectedTarget == AlignmentTarget.Comet;
    public bool IsVerifyButtonVisible => SelectedTarget == AlignmentTarget.Comet;

    private bool _useJplAstrometry;
    public bool UseJplAstrometry
    {
        get => _useJplAstrometry;
        set
        {
            if (SetProperty(ref _useJplAstrometry, value))
            {
                if (value) 
                {
                    _ = RefreshAstrometryStateAsync();
                }
                else 
                { 
                    // Reset UI se disattivato
                    AstrometryStatusMessage = ""; 
                    AstrometryStatusBrush = ColorNormal;
                    CalculateCentersCommand.NotifyCanExecuteChanged();
                }
            }
        }
    }

    private string _targetName = "";
    public string TargetName
    {
        get => _targetName;
        set => SetProperty(ref _targetName, value);
    }

    private bool _isVerifyingJpl;
    public bool IsVerifyingJpl
    {
        get => _isVerifyingJpl;
        set 
        {
            if (SetProperty(ref _isVerifyingJpl, value))
            {
                ((RelayCommand)VerifyJplCommand).NotifyCanExecuteChanged();
                CalculateCentersCommand.NotifyCanExecuteChanged();
                
                if (value) AstrometryStatusBrush = ColorLoading;
            }
        }
    }
    
    private string _astrometryStatusMessage;
    public string AstrometryStatusMessage
    {
        get => _astrometryStatusMessage;
        set => SetProperty(ref _astrometryStatusMessage, value);
    }

    private IBrush _astrometryStatusBrush = ColorNormal;
    public IBrush AstrometryStatusBrush
    {
        get => _astrometryStatusBrush;
        set => SetProperty(ref _astrometryStatusBrush, value);
    }

    public ICommand VerifyJplCommand { get; }

    // --- Macchina a Stati (UI Logic) ---
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCalculateButtonVisible))]
    [NotifyPropertyChangedFor(nameof(IsApplyCancelButtonsVisible))]
    [NotifyPropertyChangedFor(nameof(ProcessingStatusText))]
    [NotifyPropertyChangedFor(nameof(IsProcessingVisible))]
    [NotifyPropertyChangedFor(nameof(IsSearchBoxVisible))]
    [NotifyPropertyChangedFor(nameof(IsSearchRadiusControlsVisible))]
    [NotifyPropertyChangedFor(nameof(IsRefinementMessageVisible))]
    [NotifyCanExecuteChangedFor(nameof(ApplyAlignmentCommand))]
    [NotifyPropertyChangedFor(nameof(IsCoordinateListVisible))]
    [NotifyPropertyChangedFor(nameof(IsNavigationVisible))]
    [NotifyPropertyChangedFor(nameof(IsInteractionEnabled))]
    [NotifyPropertyChangedFor(nameof(IsJplOptionVisible))] 
    private AlignmentState _currentState = AlignmentState.Initial;
    
    public bool DialogResult { get; private set; }
    public event Action? RequestClose;

    public bool IsCalculateButtonVisible => CurrentState == AlignmentState.Initial;
    public bool IsApplyCancelButtonsVisible => CurrentState == AlignmentState.ResultsReady;
    public bool IsSearchBoxVisible => IsTargetMarkerVisible && CurrentState == AlignmentState.Initial;
    public bool IsSearchRadiusControlsVisible => IsSearchRadiusVisible && CurrentState == AlignmentState.Initial;
    public bool IsRefinementMessageVisible => IsSearchRadiusVisible && CurrentState != AlignmentState.Initial;
    public bool IsProcessingVisible => CurrentState == AlignmentState.Processing || CurrentState == AlignmentState.Calculating;
    public bool IsInteractionEnabled => !IsProcessingVisible;
    
    public string ProcessingStatusText => CurrentState switch
    {
        AlignmentState.Calculating => UseJplAstrometry 
            ? (SelectedTarget == AlignmentTarget.Stars ? "Analisi coordinate WCS..." : "Scaricamento dati e calcolo centri...") 
            : "Calcolo centri in corso...",
        AlignmentState.Processing => "Allineamento in corso...",
        _ => "Elaborazione..."
    };

    public bool IsNavigationVisible
    {
        get
        {
            if (!IsStack) return false;
            if (CurrentState == AlignmentState.Calculating) return false;
            if (CurrentState == AlignmentState.ResultsReady || CurrentState == AlignmentState.Processing) 
                return true;

            return SelectedTarget == AlignmentTarget.Comet && SelectedMode != AlignmentMode.Automatic;
        }
    }
    
    public bool IsCoordinateListVisible => CurrentState == AlignmentState.ResultsReady || CurrentState == AlignmentState.Processing;
    public TaskCompletionSource ImageLoadedTcs { get; } = new();

    #endregion

    #region Costruttore

    public AlignmentToolViewModel(
        List<string> sourcePaths, 
        IFitsIoService ioService,
        IFitsMetadataService metadataService,
        IAlignmentService alignmentService,
        IFitsImageDataConverter converter,      
        IImageAnalysisService analysis,
        IJplHorizonsService jplService, // <--- NUOVO PARAMETRO
        VisualizationMode initialMode = VisualizationMode.Linear)
    {
        _ioService = ioService;
        _metadataService = metadataService;
        _alignmentService = alignmentService;
        _converter = converter;
        _analysis = analysis;
        _jplService = jplService; // <--- ASSEGNAZIONE
        
        _sourcePaths = sourcePaths; 
        _totalStackCount = sourcePaths.Count;
        _currentStackIndex = 0;
        IsStack = _totalStackCount > 1;

        _visualizationMode = initialMode;

        SelectedTarget = AlignmentTarget.Comet;
        SelectedMode = AlignmentMode.Automatic;
        UseJplAstrometry = false; 
        
        Viewport.SearchRadius = this.SearchRadius;
        
        VerifyJplCommand = new RelayCommand(async () => await RefreshAstrometryStateAsync(), () => !IsVerifyingJpl && ActiveImage != null);

        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            CoordinateEntries.Clear();
            for (int i = 0; i < _totalStackCount; i++)
            {
                string fileName = System.IO.Path.GetFileName(_sourcePaths[i]);
                CoordinateEntries.Add(new CoordinateEntry { Index = i, DisplayName = fileName });
            }
            await LoadStackImageAtIndexAsync(_currentStackIndex);
            if (ActiveImage != null)
            {
                double h = ActiveImage.ImageSize.Height;
                foreach (var entry in CoordinateEntries) entry.ImageHeight = h;
            }
        }
        catch (Exception ex) { Debug.WriteLine($"AlgnVM.InitializeAsync: {ex}"); }
        finally
        {
            ImageLoadedTcs.TrySetResult();
            CalculateCentersCommand.NotifyCanExecuteChanged();
            ApplyAlignmentCommand.NotifyCanExecuteChanged();
            RefreshNavigationCommands();
        }
    }

    private async Task LoadStackImageAtIndexAsync(int index)
    {
        if (index < 0 || index >= _totalStackCount) return;
        if (index == _currentStackIndex && ActiveImage != null) return;

        _currentStackIndex = index;
        
        for (int i = 0; i < CoordinateEntries.Count; i++)
        {
            CoordinateEntries[i].IsActive = (i == _currentStackIndex);
        }
        
        UpdateStackCounterText();

        if (ActiveImage != null)
        {
            _lastContrastProfile = ActiveImage.CaptureContrastProfile();
            ActiveImage.UnloadData(); 
        }

        FitsRenderer? newRenderer = null;
        try
        {
            // FIX: LoadAsync
            var newModel = await _ioService.LoadAsync(_sourcePaths[index]);
            if (newModel != null)
            {
                // FIX: Passaggio dipendenze aggiornate
                newRenderer = new FitsRenderer(newModel, _ioService, _converter, _analysis);
                await newRenderer.InitializeAsync();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Errore load: {ex.Message}");
            newRenderer?.UnloadData(); 
            return;
        }

        if (newRenderer == null) return;

        // --- APPLICAZIONE STATO VISIVO ---
        
        newRenderer.VisualizationMode = this.VisualizationMode;

        if (_lastContrastProfile != null) 
        {
            newRenderer.ApplyContrastProfile(_lastContrastProfile);
        }
        else
        {
            Viewport.ImageSize = newRenderer.ImageSize;
            Viewport.ResetView();
            OnPropertyChanged(nameof(ZoomStatusText));
        }

        ActiveImage = newRenderer;
        
        BlackPoint = newRenderer.BlackPoint;
        WhitePoint = newRenderer.WhitePoint;

        OnPropertyChanged(nameof(CorrectImageSize));
        ResetThresholdsCommand.NotifyCanExecuteChanged();
        
        // FIX: Controllo header per OBJECT name
        if (SelectedTarget == AlignmentTarget.Comet && string.IsNullOrWhiteSpace(TargetName) && ActiveImage?.Data?.FitsHeader != null)
        {
            string headerObj = ActiveImage.Data.FitsHeader.GetStringValue("OBJECT");
            if (!string.IsNullOrWhiteSpace(headerObj)) TargetName = headerObj.Replace("'", "").Trim();
        }

        UpdateReticleVisibilityForCurrentState();
        UpdateSearchRadiusRange();
        
        ((RelayCommand)VerifyJplCommand).NotifyCanExecuteChanged();
        RefreshNavigationCommands();
    }

    public void Dispose()
    {
        ActiveImage?.UnloadData();
        ActiveImage = null; RequestClose = null; GC.SuppressFinalize(this);
    }
    private void RefreshNavigationCommands() { PreviousImageCommand.NotifyCanExecuteChanged(); NextImageCommand.NotifyCanExecuteChanged(); GoToFirstImageCommand.NotifyCanExecuteChanged(); GoToLastImageCommand.NotifyCanExecuteChanged(); }

    #endregion

    #region Comandi

    // ... (Il resto dei comandi rimane invariato) ...
    [RelayCommand] private void ZoomIn() { Viewport.ZoomIn(); OnPropertyChanged(nameof(ZoomStatusText)); }
    [RelayCommand] private void ZoomOut() { Viewport.ZoomOut(); OnPropertyChanged(nameof(ZoomStatusText)); }
    [RelayCommand] private void ResetView() { Viewport.ResetView(); OnPropertyChanged(nameof(ZoomStatusText)); }
    [RelayCommand(CanExecute = nameof(CanResetThresholds))] private async Task ResetThresholds() { if (ActiveImage == null) return; await ActiveImage.ResetThresholdsAsync(); BlackPoint = ActiveImage.BlackPoint; WhitePoint = ActiveImage.WhitePoint; }
    private bool CanResetThresholds() => ActiveImage != null;
    private async Task NavigateToImage(int newIndex) { if (newIndex < 0 || newIndex >= _totalStackCount || newIndex == _currentStackIndex) return; TargetCoordinate = null; await LoadStackImageAtIndexAsync(newIndex); }
    private int? GetPreviousAccessibleIndex() { for (int i = _currentStackIndex - 1; i >= 0; i--) if (IsIndexAccessible(i)) return i; return null; }
    private int? GetNextAccessibleIndex() { for (int i = _currentStackIndex + 1; i < _totalStackCount; i++) if (IsIndexAccessible(i)) return i; return null; }
    private bool CanShowPrevious() => IsStack && GetPreviousAccessibleIndex().HasValue;
    private bool CanShowNext() => IsStack && GetNextAccessibleIndex().HasValue;
    [RelayCommand(CanExecute = nameof(CanShowPrevious))] private async Task PreviousImage() { var prev = GetPreviousAccessibleIndex(); if (prev.HasValue) await NavigateToImage(prev.Value); }
    [RelayCommand(CanExecute = nameof(CanShowNext))] private async Task NextImage() { var next = GetNextAccessibleIndex(); if (next.HasValue) await NavigateToImage(next.Value); }
    [RelayCommand(CanExecute = nameof(CanShowPrevious))] private async Task GoToFirstImage() => await NavigateToImage(0);
    [RelayCommand(CanExecute = nameof(CanShowNext))] private async Task GoToLastImage() => await NavigateToImage(_totalStackCount - 1);
    
    // ... (Alignment Logic) ...
[RelayCommand(CanExecute = nameof(CanCalculateCenters))]
private async Task CalculateCenters()
{
    CurrentState = AlignmentState.Calculating; 
    OnPropertyChanged(nameof(ProcessingStatusText)); 

    try
    {
        // 1. Logica di Pre-Calcolo (UI Point)
        List<Point?> startingPointsUI;

        if (SelectedTarget == AlignmentTarget.Stars && UseJplAstrometry)
        {
            startingPointsUI = await PreCalculateSkyFixedPointsAsync();
        }
        else if (SelectedTarget == AlignmentTarget.Comet && UseJplAstrometry && SelectedMode == AlignmentMode.Automatic)
        {
            startingPointsUI = await PreCalculateJplCentersAsync();
        }
        else if (SelectedTarget == AlignmentTarget.Comet && UseJplAstrometry && SelectedMode == AlignmentMode.Guided)
        {
            var nasaTrajectory = await PreCalculateJplCentersAsync();
            var userStart = CoordinateEntries.First().Coordinate;
            var userEnd = CoordinateEntries.Last().Coordinate;

            if (nasaTrajectory.Count == _totalStackCount && 
                userStart.HasValue && userEnd.HasValue &&
                nasaTrajectory[0].HasValue && nasaTrajectory[^1].HasValue)
            {
                startingPointsUI = new List<Point?>();
                
                Point startOffset = userStart.Value - nasaTrajectory[0].Value;
                Point endOffset = userEnd.Value - nasaTrajectory[^1].Value;

                for (int frameIndex = 0; frameIndex < _totalStackCount; frameIndex++)
                {
                    if (frameIndex == 0) startingPointsUI.Add(userStart);
                    else if (frameIndex == _totalStackCount - 1) startingPointsUI.Add(userEnd);
                    else
                    {
                        if (nasaTrajectory[frameIndex].HasValue)
                        {
                            double t = (double)frameIndex / (_totalStackCount - 1);
                            double ox = startOffset.X + (endOffset.X - startOffset.X) * t;
                            double oy = startOffset.Y + (endOffset.Y - startOffset.Y) * t;
                            
                            Point nasaPt = nasaTrajectory[frameIndex]!.Value;
                            Point finalPt = new Point(nasaPt.X + ox, nasaPt.Y + oy);
                            startingPointsUI.Add(finalPt);
                        }
                        else 
                        {
                            startingPointsUI.Add(null);
                        }
                    }
                }
            }
            else 
            {
                startingPointsUI = CoordinateEntries.Select(e => e.Coordinate).ToList();
            }
        }
        else 
        {
            if (SelectedTarget == AlignmentTarget.Stars)
            {
                startingPointsUI = Enumerable.Repeat<Point?>(null, _totalStackCount).ToList();
            }
            else
            {
                startingPointsUI = CoordinateEntries.Select(e => e.Coordinate).ToList();
                if (startingPointsUI.All(p => p == null) && ActiveImage != null)
                {
                    var centerImg = new Point(ActiveImage.ImageSize.Width / 2, ActiveImage.ImageSize.Height / 2);
                    startingPointsUI = Enumerable.Repeat<Point?>(centerImg, _totalStackCount).ToList();
                }
            }
        }

        // 2. CONVERSIONE UI -> DOMAIN (FIX: Cast a Point2D?)
        var domainStartingPoints = startingPointsUI
            .Select(p => p.HasValue ? (Point2D?)new Point2D(p.Value.X, p.Value.Y) : null)
            .ToList();

        // 3. Gestione Progress
        var progressHandler = new Progress<(int Index, Point2D? Center)>(update =>
        {
            if (update.Index >= 0 && update.Index < CoordinateEntries.Count)
            {
                CoordinateEntries[update.Index].Coordinate = update.Center.HasValue 
                    ? new Point(update.Center.Value.X, update.Center.Value.Y) 
                    : null;
            }
        });

        // 4. Chiamata al Servizio
        var newCoordsDomain = await _alignmentService.CalculateCentersAsync(
            SelectedTarget,
            SelectedMode, 
            CenteringMethod.LocalRegion,
            _sourcePaths, 
            domainStartingPoints, 
            SearchRadius,
            progressHandler 
        );

        // 5. CONVERSIONE DOMAIN -> UI
        int resultIdx = 0;
        foreach (var coord in newCoordsDomain)
        {
            if (resultIdx < CoordinateEntries.Count)
            {
                CoordinateEntries[resultIdx].Coordinate = coord.HasValue 
                    ? new Point(coord.Value.X, coord.Value.Y) 
                    : null;
            }
            resultIdx++;
        }

        CurrentState = AlignmentState.ResultsReady;
        UpdateReticleVisibilityForCurrentState();
        ApplyAlignmentCommand.NotifyCanExecuteChanged();
    }
    catch (Exception ex)
    {
        Debug.WriteLine($"Errore calcolo: {ex}");
        AstrometryStatusMessage = $"Errore di calcolo: {ex.Message}";
        AstrometryStatusBrush = ColorError;
        CurrentState = AlignmentState.Initial;
    }
}

private bool CanCalculateCenters()
{
    if (UseJplAstrometry)
    {
        if (IsVerifyingJpl) return false;
        if (!_isAstrometryValid) return false;
    }

    // Convertiamo i punti UI in Point2D per il servizio
    var currentCoordsDomain = CoordinateEntries
        .Select(e => e.Coordinate.HasValue ? new Point2D(e.Coordinate.Value.X, e.Coordinate.Value.Y) : (Point2D?)null)
        .ToList();

    return _alignmentService.CanCalculate(SelectedTarget, SelectedMode, currentCoordsDomain, _totalStackCount);
}

[RelayCommand(CanExecute = nameof(CanApplyAlignment))]
private async Task ApplyAlignment()
{
    CurrentState = AlignmentState.Processing;
    try
    {
        // Conversione UI -> Domain
        var centersDomain = CoordinateEntries
            .Select(e => e.Coordinate.HasValue ? new Point2D(e.Coordinate.Value.X, e.Coordinate.Value.Y) : (Point2D?)null)
            .ToList();

        string tempFolder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Komalab", "Aligned");
        
        FinalProcessedPaths = await _alignmentService.ApplyCenteringAndSaveAsync(
            _sourcePaths, 
            centersDomain, // Passiamo Point2D
            tempFolder, 
            SelectedTarget
        );

        DialogResult = true; 
        RequestClose?.Invoke();
    }
    catch (Exception ex) 
    { 
        Debug.WriteLine($"Apply fallito: {ex.Message}"); 
        DialogResult = false; 
        CurrentState = AlignmentState.Initial; 
    }
}
    
    [RelayCommand] 
    private void CancelCalculation() 
    {
        ResetAlignmentState();
        if (UseJplAstrometry) _ = RefreshAstrometryStateAsync();
    }
    
    private bool CanApplyAlignment() => CurrentState == AlignmentState.ResultsReady;

    // --- AGGIORNAMENTO STATO ASTROMETRICO ---
    private async Task RefreshAstrometryStateAsync()
{
    // 1. Reset Stato UI
    IsVerifyingJpl = true;
    _isAstrometryValid = false;
    
    // Aggiorniamo il comando per disabilitare il tasto "Calcola" mentre verifichiamo
    CalculateCentersCommand.NotifyCanExecuteChanged();

    // Caso speciale: La modalità Manuale non richiede verifiche esterne (WCS o JPL)
    if (SelectedTarget == AlignmentTarget.Comet && SelectedMode == AlignmentMode.Manual)
    {
        AstrometryStatusMessage = "";
        IsVerifyingJpl = false;
        return; 
    }

    // Controllo preliminare: deve esserci almeno un'immagine caricata
    if (ActiveImage == null || _sourcePaths.Count == 0)
    {
        AstrometryStatusMessage = "Nessuna immagine caricata.";
        AstrometryStatusBrush = ColorError;
        IsVerifyingJpl = false;
        return;
    }

    AstrometryStatusBrush = ColorLoading;
    
    try
    {
        // =========================================================
        // CASO 1: STELLE (Solo WCS)
        // =========================================================
        if (SelectedTarget == AlignmentTarget.Stars)
        {
            AstrometryStatusMessage = "Verifica dati WCS (Header)...";
            
            // Per le stelle verifichiamo se l'immagine corrente ha un WCS valido
            // e se è possibile proiettare le coordinate celesti sui pixel.
            var p = await FetchSkyCoordinateForImage(_currentStackIndex);
            
            if (p.HasValue)
            {
                // Se stavamo puntando manualmente, rimuoviamo il marker per evitare confusione
                if (IsTargetMarkerVisible) TargetCoordinate = null; 
                
                AstrometryStatusMessage = "WCS valido. Allineamento possibile.";
                AstrometryStatusBrush = ColorSuccess;
                _isAstrometryValid = true;
            }
            else
            {
                throw new InvalidOperationException("Dati WCS mancanti o incoerenti nell'header.");
            }
        }
        // =========================================================
        // CASO 2: COMETA (JPL Horizons + WCS + DATE-OBS)
        // =========================================================
        else if (SelectedTarget == AlignmentTarget.Comet)
        {
            AstrometryStatusMessage = "Verifica integrità stack...";

            // -------------------------------------------------------------
            // FASE A: VALIDAZIONE TECNICA DI TUTTI GLI HEADER
            // -------------------------------------------------------------
            // Prima di chiamare la NASA, ci assicuriamo che TUTTI i file locali
            // abbiano i requisiti minimi (Data e WCS).
            for (int i = 0; i < _sourcePaths.Count; i++)
            {
                // Uso del servizio IO Enterprise per leggere solo l'header
                var h = await _ioService.ReadHeaderOnlyAsync(_sourcePaths[i]);
                string fName = System.IO.Path.GetFileName(_sourcePaths[i]);

                if (h == null) 
                    throw new InvalidOperationException($"Il file '{fName}' non ha un header FITS valido.");
                    
                // Verifica Data tramite MetadataService
                if (_metadataService.GetObservationDate(h) == null)
                    throw new InvalidOperationException($"Il file '{fName}' (frame {i+1}) non ha una data valida (DATE-OBS).");
                    
                // Verifica WCS tramite il Parser
                var w = KomaLab.Services.Data.Parsers.WcsParser.Parse(h);
                if (w == null || !w.IsValid)
                    throw new InvalidOperationException($"Il file '{fName}' (frame {i+1}) non ha dati WCS validi (Esegui Plate Solving).");
            }

            // Se arriviamo qui, i file sono tecnicamente perfetti.
            AstrometryStatusMessage = "Connessione a NASA JPL in corso...";

            // -------------------------------------------------------------
            // FASE B: LOGICA DI RETE SPECIFICA PER MODALITÀ
            // -------------------------------------------------------------
            if (SelectedMode == AlignmentMode.Guided)
            {
                // In modalità guidata ci servono almeno il primo e l'ultimo punto
                var pStart = await FetchJplCoordinateForImage(0);
                var pEnd = await FetchJplCoordinateForImage(_totalStackCount - 1);

                if (pStart.HasValue && pEnd.HasValue)
                {
                    CoordinateEntries[0].Coordinate = pStart;
                    CoordinateEntries[_totalStackCount - 1].Coordinate = pEnd;
                    
                    // Posizioniamo visivamente il reticolo se l'utente sta guardando Start o End
                    if (_currentStackIndex == 0) TargetCoordinate = pStart;
                    else if (_currentStackIndex == _totalStackCount - 1) TargetCoordinate = pEnd;
                    else TargetCoordinate = null; 

                    AstrometryStatusMessage = $"Traiettoria confermata su {_totalStackCount} immagini.";
                    AstrometryStatusBrush = ColorSuccess;
                    _isAstrometryValid = true;
                }
                else
                {
                    // Dati tecnici OK, ma coordinate JPL fuori immagine (FOV)
                    AstrometryStatusMessage = "Oggetto calcolato fuori dal campo visivo nel primo o ultimo frame.";
                    AstrometryStatusBrush = ColorError;
                    _isAstrometryValid = false;
                }
            }
            else if (SelectedMode == AlignmentMode.Automatic)
            {
                // In automatico verifichiamo l'immagine corrente per dare feedback immediato
                var p = await FetchJplCoordinateForImage(_currentStackIndex);
                
                if (p.HasValue)
                {
                    if (IsTargetMarkerVisible) TargetCoordinate = null;
                    
                    AstrometryStatusMessage = $"Dati validi per tutte le {_totalStackCount} immagini. Oggetto nel campo.";
                    AstrometryStatusBrush = ColorSuccess;
                    _isAstrometryValid = true;
                }
                else
                {
                    AstrometryStatusMessage = "Oggetto non trovato nel campo visivo (FOV) dell'immagine corrente.";
                    AstrometryStatusBrush = ColorError;
                    _isAstrometryValid = false;
                }
            }
        }
    }
    // =========================================================
    // GESTIONE ERRORI UNIFICATA
    // =========================================================
    catch (InvalidOperationException ex)
    {
        // Errori sui Dati Locali (Header mancanti, WCS errato, Date mancanti)
        AstrometryStatusMessage = $"Dati mancanti: {ex.Message}";
        AstrometryStatusBrush = ColorError;
        _isAstrometryValid = false;
    }
    catch (System.Net.Http.HttpRequestException)
    {
        // Errori di Rete (JPL Down o assenza internet)
        AstrometryStatusMessage = "Errore di rete: Impossibile contattare NASA JPL.";
        AstrometryStatusBrush = ColorError;
        _isAstrometryValid = false;
    }
    catch (Exception ex)
    {
        // Errori Generici imprevisti
        AstrometryStatusMessage = $"Errore imprevisto: {ex.Message}";
        AstrometryStatusBrush = ColorError;
        _isAstrometryValid = false;
    }
    finally
    {
        IsVerifyingJpl = false;
        // Aggiorniamo lo stato del bottone "Calcola"
        CalculateCentersCommand.NotifyCanExecuteChanged();
    }
}

    private void UpdateReticle(Point point)
    {
        TargetCoordinate = point;
        CoordinateEntries[_currentStackIndex].Coordinate = point;
        ApplyAlignmentCommand.NotifyCanExecuteChanged();
        CalculateCentersCommand.NotifyCanExecuteChanged();
    }

    private async Task<List<Point?>> PreCalculateJplCentersAsync()
    {
        var results = new List<Point?>();
        for (int i = 0; i < _sourcePaths.Count; i++) results.Add(await FetchJplCoordinateForImage(i));
        return results;
    }

    private async Task<Point?> FetchJplCoordinateForImage(int index)
    {
        string path = _sourcePaths[index];
        
        // 1. Lettura Header
        var header = await _ioService.ReadHeaderOnlyAsync(path);
        if (header == null) 
            throw new InvalidOperationException($"Impossibile leggere l'header del file {System.IO.Path.GetFileName(path)}");

        // 2. Data Osservazione
        var obsDate = _metadataService.GetObservationDate(header);
        if (obsDate == null) 
            throw new InvalidOperationException("Header 'DATE-OBS' mancante o non valido.");

        // 3. WCS
        var wcsData = KomaLab.Services.Data.Parsers.WcsParser.Parse(header);
        if (wcsData == null || !wcsData.IsValid) 
            throw new InvalidOperationException("Dati WCS (calibrazione astrometrica) mancanti o incompleti.");

        try 
        {
            // 4. Location Osservatorio
            var location = KomaLab.Services.Data.Parsers.GeographicParser.ParseLocation(header);
            
            // FIX: Usiamo il servizio iniettato, non 'new'
            var ephem = await _jplService.GetEphemerisAsync(TargetName, obsDate.Value, location);

            if (ephem.HasValue)
            {
                int height = header.GetIntValue("NAXIS2");
                var transform = new WcsTransformation(wcsData);
                var pixel = transform.WorldToPixel(ephem.Value.Ra, ephem.Value.Dec);
                
                // Conversione coordinate WCS (Y in basso) -> Avalonia (Y in alto) se necessario, 
                // ma di solito WorldToPixel rispetta già la convenzione FITS.
                // Controlla se 'height - pixel.Y' è corretto per il tuo sistema di coordinate visive.
                if (pixel.HasValue) 
                {
                    // Avalonia ha (0,0) in alto a sinistra. FITS ha (0,0) in basso a sinistra (solitamente).
                    // Se WcsTransformation restituisce coord FITS, devi invertire la Y.
                    return new Point(pixel.Value.X, height - pixel.Value.Y);
                }
            }
            
            return null; 
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new Exception($"Errore durante il calcolo JPL: {ex.Message}");
        }
    }

    private async Task<Point?> FetchSkyCoordinateForImage(int index)
    {
        try
        {
            if (_sourcePaths.Count == 0) return null;

            // FIX: ReadHeaderOnlyAsync + WcsParser
            var refHeader = await _ioService.ReadHeaderOnlyAsync(_sourcePaths[0]);
            var refWcs = KomaLab.Services.Data.Parsers.WcsParser.Parse(refHeader);
            if (refWcs == null || !refWcs.IsValid) return null; 

            double anchorRa = refWcs.RefRaDeg; 
            double anchorDec = refWcs.RefDecDeg;

            var header = await _ioService.ReadHeaderOnlyAsync(_sourcePaths[index]);
            int height = header?.GetIntValue("NAXIS2") ?? 0;
            var wcs = KomaLab.Services.Data.Parsers.WcsParser.Parse(header);
            
            if (wcs != null && wcs.IsValid) 
            {
                var transform = new WcsTransformation(wcs);
                var pixel = transform.WorldToPixel(anchorRa, anchorDec);
                
                if (pixel.HasValue) 
                {
                    double finalY = height - pixel.Value.Y;
                    return new Point(pixel.Value.X, finalY);
                }
            }
            return null;
        }
        catch { return null; }
    }

    private async Task<List<Point?>> PreCalculateSkyFixedPointsAsync()
    {
        var rawPoints = new List<Point?>();
        for (int i = 0; i < _sourcePaths.Count; i++) 
        {
            rawPoints.Add(await FetchSkyCoordinateForImage(i));
        }

        if (rawPoints.Count > 0 && rawPoints[0].HasValue)
        {
            try 
            {
                // FIX: ReadHeaderOnlyAsync
                var header0 = await _ioService.ReadHeaderOnlyAsync(_sourcePaths[0]);
                if (header0 != null)
                {
                    double w = header0.GetIntValue("NAXIS1");
                    double h = header0.GetIntValue("NAXIS2");
                    
                    var geometricCenter = new Point(w / 2.0, h / 2.0);
                    var wcsPoint0 = rawPoints[0]!.Value;
                    var offsetVector = geometricCenter - wcsPoint0;

                    var optimizedPoints = new List<Point?>();
                    for (int i = 0; i < rawPoints.Count; i++)
                    {
                        if (rawPoints[i].HasValue)
                        {
                            optimizedPoints.Add(rawPoints[i]!.Value + offsetVector);
                        }
                        else
                        {
                            optimizedPoints.Add(null);
                        }
                    }
                    return optimizedPoints;
                }
            }
            catch { return rawPoints; }
        }

        return rawPoints;
    }

    #endregion
    
    #region Metodi Pubblici
    public void ApplyZoomAtPoint(double scaleFactor, Point viewportZoomPoint) { Viewport.ApplyZoomAtPoint(scaleFactor, viewportZoomPoint); OnPropertyChanged(nameof(ZoomStatusText)); }
    public void ApplyPan(double deltaX, double deltaY) => Viewport.ApplyPan(deltaX, deltaY);
    public void SetTargetCoordinate(Point imageCoordinate) 
    { 
        if (CurrentState == AlignmentState.Processing) return;
        if (CurrentState == AlignmentState.Initial && IsStack && SelectedMode == AlignmentMode.Guided && SelectedTarget == AlignmentTarget.Comet) 
        { 
            if (_currentStackIndex != 0 && _currentStackIndex != (_totalStackCount - 1)) return; 
        }
        TargetCoordinate = imageCoordinate; 
        if (_currentStackIndex >= 0 && _currentStackIndex < CoordinateEntries.Count) CoordinateEntries[_currentStackIndex].Coordinate = imageCoordinate;
        ApplyAlignmentCommand.NotifyCanExecuteChanged(); CalculateCentersCommand.NotifyCanExecuteChanged(); 
    }
    public void ClearTarget() 
    { 
        if (CurrentState != AlignmentState.Initial) { ResetAlignmentState(); return; }
        if (SelectedMode == AlignmentMode.Automatic && SelectedTarget == AlignmentTarget.Comet) return;
        if (IsStack && SelectedMode == AlignmentMode.Guided && SelectedTarget == AlignmentTarget.Comet) { if (_currentStackIndex != 0 && _currentStackIndex != (_totalStackCount - 1)) return; }
        TargetCoordinate = null; 
        if (_currentStackIndex >= 0 && _currentStackIndex < CoordinateEntries.Count) CoordinateEntries[_currentStackIndex].Coordinate = null;
        ApplyAlignmentCommand.NotifyCanExecuteChanged(); CalculateCentersCommand.NotifyCanExecuteChanged(); 
    }
    #endregion
    
    #region Helpers
    partial void OnBlackPointChanged(double value) { if (ActiveImage != null) ActiveImage.BlackPoint = value; }
    partial void OnWhitePointChanged(double value) { if (ActiveImage != null) ActiveImage.WhitePoint = value; }
    partial void OnSearchRadiusChanged(int value) => Viewport.SearchRadius = value;
    partial void OnTargetCoordinateChanged(Point? value) => Viewport.TargetCoordinate = value;
    
    partial void OnSelectedTargetChanged(AlignmentTarget value)
    {
        SelectedMode = AlignmentMode.Automatic;
        UseJplAstrometry = false; 

        OnPropertyChanged(nameof(AvailableAlignmentModes));
        OnPropertyChanged(nameof(SelectedMode)); 
        OnPropertyChanged(nameof(IsJplOptionVisible));
        
        OnPropertyChanged(nameof(AstrometryOptionLabel));
        OnPropertyChanged(nameof(IsTargetNameInputVisible));
        OnPropertyChanged(nameof(IsVerifyButtonVisible)); 
    }
    
    partial void OnSelectedModeChanged(AlignmentMode value)
    {
        ResetAlignmentState();
        RefreshNavigationCommands();
        if (!IsIndexAccessible(_currentStackIndex)) _ = NavigateToImage(0); 
        UpdateStackCounterText();
        OnPropertyChanged(nameof(IsJplOptionVisible)); 

        if (UseJplAstrometry)
        {
            _ = RefreshAstrometryStateAsync();
        }
    }
    
    partial void OnCurrentStateChanged(AlignmentState value) 
    { 
        RefreshNavigationCommands(); 
        UpdateStackCounterText();
        OnPropertyChanged(nameof(IsJplOptionVisible)); 
        OnPropertyChanged(nameof(IsSearchRadiusControlsVisible));
    }
    
    private void ResetAlignmentState() { CurrentState = AlignmentState.Initial; foreach (var entry in CoordinateEntries) entry.Coordinate = null; TargetCoordinate = null; CalculateCentersCommand.NotifyCanExecuteChanged(); ApplyAlignmentCommand.NotifyCanExecuteChanged(); }
    private void UpdateReticleVisibilityForCurrentState() { if (_currentStackIndex < 0 || _currentStackIndex >= CoordinateEntries.Count) { TargetCoordinate = null; return; } var currentEntry = CoordinateEntries[_currentStackIndex]; TargetCoordinate = currentEntry.Coordinate; }
    private void UpdateSearchRadiusRange() { if (ActiveImage == null || ActiveImage.ImageSize == default(Size)) { MinSearchRadius = 0; MaxSearchRadius = 100; } else { double minDimension = Math.Min(ActiveImage.ImageSize.Width, ActiveImage.ImageSize.Height); MinSearchRadius = 0; MaxSearchRadius = (int)Math.Floor(minDimension / 2.0); } SearchRadius = Math.Clamp(SearchRadius, MinSearchRadius, MaxSearchRadius); }
    
    private bool IsIndexAccessible(int index) { 
        if (CurrentState != AlignmentState.Initial) return true;
        if (index < 0 || index >= _totalStackCount) return false;
        if (SelectedTarget == AlignmentTarget.Stars) return index == 0;
        return SelectedMode switch {
            AlignmentMode.Automatic => index == 0,
            AlignmentMode.Guided => index == 0 || index == _totalStackCount - 1,
            _ => true
        };
    }
    private void UpdateStackCounterText() {
        if (CurrentState != AlignmentState.Initial || SelectedMode == AlignmentMode.Manual) { StackCounterText = $"{_currentStackIndex + 1} / {_totalStackCount}"; return; }
        if (SelectedMode == AlignmentMode.Automatic || SelectedTarget == AlignmentTarget.Stars) { StackCounterText = "1 / 1"; return; }
        if (SelectedMode == AlignmentMode.Guided) { int visibleStep = (_currentStackIndex == 0) ? 1 : 2; StackCounterText = $"{visibleStep} / 2"; }
    }
    #endregion
}