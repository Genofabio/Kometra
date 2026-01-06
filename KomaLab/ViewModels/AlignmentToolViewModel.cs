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
    
    private readonly IFitsService _fitsService;
    private readonly IAlignmentService _alignmentService;
    private readonly IFitsDataConverter _converter;
    private readonly IImageAnalysisService _analysis;
    
    private readonly List<string> _sourcePaths; 
    private int _currentStackIndex;
    private readonly int _totalStackCount;
    
    private Size _viewportSize;
    private ContrastProfile? _lastContrastProfile;
    
    private bool _isAstrometryValid = false;

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

    [ObservableProperty] private double _blackPoint;
    [ObservableProperty] private double _whitePoint;
    
    // --- NUOVO: Stato Visualizzazione ---
    [ObservableProperty]
    private VisualizationMode _visualizationMode = VisualizationMode.Linear;

    // Se cambia (es. se mettessimo una combo anche qui), aggiorniamo l'immagine attiva
    partial void OnVisualizationModeChanged(VisualizationMode value)
    {
        if (ActiveImage != null) ActiveImage.VisualizationMode = value;
    }

    public string ZoomStatusText => $"{Viewport.Scale:P0}";
    public bool IsTargetMarkerVisible => TargetCoordinate.HasValue;
    
    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(IsTargetMarkerVisible))]
    [NotifyPropertyChangedFor(nameof(IsSearchBoxVisible))]
    private Point? _targetCoordinate;

    [ObservableProperty] private bool _isStack;
    [ObservableProperty] private string _stackCounterText = "";
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
    
    public bool IsSearchRadiusVisible => SelectedTarget == AlignmentTarget.Comet && SelectedMode != AlignmentMode.Automatic;

    [ObservableProperty] private int _minSearchRadius;
    [ObservableProperty] private int _maxSearchRadius = 100;
    [ObservableProperty] private int _searchRadius = 100;

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
        IFitsService fitsService,
        IAlignmentService alignmentService,
        IFitsDataConverter converter,      
        IImageAnalysisService analysis,
        VisualizationMode initialMode = VisualizationMode.Linear) // <-- NUOVO PARAMETRO
    {
        _fitsService = fitsService;
        _alignmentService = alignmentService;
        _converter = converter;
        _analysis = analysis;
        
        _sourcePaths = sourcePaths; 
        _totalStackCount = sourcePaths.Count;
        _currentStackIndex = 0;
        IsStack = _totalStackCount > 1;

        // Impostiamo il modo ereditato dal nodo
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
            var newModel = await _fitsService.LoadFitsFromFileAsync(_sourcePaths[index]);
            if (newModel != null)
            {
                newRenderer = new FitsRenderer(newModel, _fitsService, _converter, _analysis);
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
        
        // 1. Applica il modo di visualizzazione (Log, Sqrt, ecc.) ereditato o corrente
        newRenderer.VisualizationMode = this.VisualizationMode;

        // 2. Applica il profilo di contrasto (Slider)
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
        
        // Aggiorna slider UI
        BlackPoint = newRenderer.BlackPoint;
        WhitePoint = newRenderer.WhitePoint;

        OnPropertyChanged(nameof(CorrectImageSize));
        ResetThresholdsCommand.NotifyCanExecuteChanged();
        
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
            List<Point?> startingPoints;

            if (SelectedTarget == AlignmentTarget.Stars && UseJplAstrometry)
            {
                startingPoints = await PreCalculateSkyFixedPointsAsync();
            }
            else if (SelectedTarget == AlignmentTarget.Comet && UseJplAstrometry && SelectedMode == AlignmentMode.Automatic)
            {
                startingPoints = await PreCalculateJplCentersAsync();
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
                    startingPoints = new List<Point?>();
                    
                    Point startOffset = userStart.Value - nasaTrajectory[0].Value;
                    Point endOffset = userEnd.Value - nasaTrajectory[^1].Value;

                    for (int frameIndex = 0; frameIndex < _totalStackCount; frameIndex++)
                    {
                        if (frameIndex == 0) startingPoints.Add(userStart);
                        else if (frameIndex == _totalStackCount - 1) startingPoints.Add(userEnd);
                        else
                        {
                            if (nasaTrajectory[frameIndex].HasValue)
                            {
                                double t = (double)frameIndex / (_totalStackCount - 1);
                                double ox = startOffset.X + (endOffset.X - startOffset.X) * t;
                                double oy = startOffset.Y + (endOffset.Y - startOffset.Y) * t;
                                
                                Point nasaPt = nasaTrajectory[frameIndex]!.Value;
                                Point finalPt = new Point(nasaPt.X + ox, nasaPt.Y + oy);
                                startingPoints.Add(finalPt);
                            }
                            else 
                            {
                                startingPoints.Add(null);
                            }
                        }
                    }
                }
                else 
                {
                    startingPoints = CoordinateEntries.Select(e => e.Coordinate).ToList();
                }
            }
            else 
            {
                if (SelectedTarget == AlignmentTarget.Stars)
                {
                    startingPoints = Enumerable.Repeat<Point?>(null, _totalStackCount).ToList();
                }
                else
                {
                    startingPoints = CoordinateEntries.Select(e => e.Coordinate).ToList();
                    if (startingPoints.All(p => p == null) && ActiveImage != null)
                    {
                        var centerImg = new Point(ActiveImage.ImageSize.Width / 2, ActiveImage.ImageSize.Height / 2);
                        startingPoints = Enumerable.Repeat<Point?>(centerImg, _totalStackCount).ToList();
                    }
                }
            }

            var progressHandler = new Progress<(int Index, Point? Center)>(update =>
            {
                if (update.Index >= 0 && update.Index < CoordinateEntries.Count)
                    CoordinateEntries[update.Index].Coordinate = update.Center;
            });

            var newCoords = await _alignmentService.CalculateCentersAsync(
                SelectedTarget,
                SelectedMode, 
                CenteringMethod.LocalRegion,
                _sourcePaths, 
                startingPoints, 
                SearchRadius,
                progressHandler 
            );

            int resultIdx = 0;
            foreach (var coord in newCoords)
            {
                if (resultIdx < CoordinateEntries.Count) CoordinateEntries[resultIdx].Coordinate = coord;
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

        var currentCoords = CoordinateEntries.Select(e => e.Coordinate).ToList();
        return _alignmentService.CanCalculate(SelectedTarget, SelectedMode, currentCoords, _totalStackCount);
    }

    [RelayCommand(CanExecute = nameof(CanApplyAlignment))]
    private async Task ApplyAlignment()
    {
        CurrentState = AlignmentState.Processing;
        try
        {
            var centers = CoordinateEntries.Select(e => e.Coordinate).ToList();
            string tempFolder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Komalab", "Aligned");
            FinalProcessedPaths = await _alignmentService.ApplyCenteringAndSaveAsync(_sourcePaths, centers, tempFolder, SelectedTarget);
            DialogResult = true; RequestClose?.Invoke();
        }
        catch (Exception ex) { Debug.WriteLine($"Apply fallito: {ex.Message}"); DialogResult = false; CurrentState = AlignmentState.Initial; }
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
        IsVerifyingJpl = true;
        _isAstrometryValid = false;
        CalculateCentersCommand.NotifyCanExecuteChanged();

        if (SelectedTarget == AlignmentTarget.Comet && SelectedMode == AlignmentMode.Manual)
        {
            AstrometryStatusMessage = "";
            IsVerifyingJpl = false;
            return; 
        }

        if (ActiveImage?.Data?.FitsHeader == null)
        {
            AstrometryStatusMessage = "Errore: Nessuna immagine caricata.";
            AstrometryStatusBrush = ColorError;
            IsVerifyingJpl = false;
            return;
        }

        AstrometryStatusBrush = ColorLoading;
        bool isSuccess = false;

        try
        {
            if (SelectedTarget == AlignmentTarget.Stars)
            {
                AstrometryStatusMessage = "Verifica dati WCS (Header)...";
                var p = await FetchSkyCoordinateForImage(_currentStackIndex);
                if (p.HasValue)
                {
                    if (IsTargetMarkerVisible) TargetCoordinate = null; 
                    isSuccess = true;
                    AstrometryStatusMessage = "WCS valido. Allineamento possibile.";
                }
                else
                {
                    AstrometryStatusMessage = "Dati WCS mancanti o incoerenti.";
                }
            }
            else if (SelectedTarget == AlignmentTarget.Comet)
            {
                AstrometryStatusMessage = "Connessione a NASA JPL in corso...";

                if (SelectedMode == AlignmentMode.Guided)
                {
                    var pStart = await FetchJplCoordinateForImage(0);
                    var pEnd = await FetchJplCoordinateForImage(_totalStackCount - 1);

                    if (pStart.HasValue && pEnd.HasValue)
                    {
                        CoordinateEntries[0].Coordinate = pStart;
                        CoordinateEntries[_totalStackCount - 1].Coordinate = pEnd;
                        
                        if (_currentStackIndex == 0) TargetCoordinate = pStart;
                        else if (_currentStackIndex == _totalStackCount - 1) TargetCoordinate = pEnd;
                        else TargetCoordinate = null; 

                        isSuccess = true;
                    }
                }
                else if (SelectedMode == AlignmentMode.Automatic)
                {
                    var p = await FetchJplCoordinateForImage(_currentStackIndex);
                    if (p.HasValue)
                    {
                        if (IsTargetMarkerVisible) TargetCoordinate = null;
                        isSuccess = true;
                    }
                }

                if (isSuccess) AstrometryStatusMessage = "Dati NASA/JPL acquisiti correttamente.";
                else AstrometryStatusMessage = "Oggetto non trovato o fuori campo FOV.";
            }

            if (isSuccess)
            {
                AstrometryStatusBrush = ColorSuccess;
                _isAstrometryValid = true;
            }
            else
            {
                if (!AstrometryStatusMessage.Contains("Errore") && !AstrometryStatusMessage.Contains("mancanti") && !AstrometryStatusMessage.Contains("fuori campo"))
                {
                     AstrometryStatusMessage = "Impossibile calcolare il riferimento.";
                }
                AstrometryStatusBrush = ColorError;
                _isAstrometryValid = false;
            }
        }
        catch (System.Net.Http.HttpRequestException)
        {
            AstrometryStatusMessage = "Errore di rete: Impossibile contattare NASA JPL.";
            AstrometryStatusBrush = ColorError;
            _isAstrometryValid = false;
        }
        catch (Exception ex)
        {
            AstrometryStatusMessage = $"Errore: {ex.Message}";
            AstrometryStatusBrush = ColorError;
            _isAstrometryValid = false;
        }
        finally
        {
            IsVerifyingJpl = false;
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
        try 
        {
            string path = _sourcePaths[index];
            var header = await _fitsService.ReadHeaderOnlyAsync(path);
            if (header == null) return null;

            int height = header.GetIntValue("NAXIS2");
            string dateObsStr = header.GetStringValue("DATE-OBS");
            if (!DateTime.TryParse(dateObsStr, out DateTime obsDate)) return null;
            
            var location = FitsMetadataReader.ReadObservatoryLocation(header);
            var wcsData = WcsHeaderParser.Parse(header);
            if (!wcsData.IsValid) return null;

            var jplService = new JplHorizonsService();
            var ephem = await jplService.GetEphemerisAsync(TargetName, obsDate, location);

            if (ephem.HasValue)
            {
                var transform = new WcsTransformation(wcsData);
                var pixel = transform.WorldToPixel(ephem.Value.Ra, ephem.Value.Dec);
                if (pixel.HasValue) return new Point(pixel.Value.X, height - pixel.Value.Y);
            }
            return null;
        }
        catch { return null; }
    }

    private async Task<Point?> FetchSkyCoordinateForImage(int index)
    {
        try
        {
            if (_sourcePaths.Count == 0) return null;

            var refHeader = await _fitsService.ReadHeaderOnlyAsync(_sourcePaths[0]);
            var refWcs = WcsHeaderParser.Parse(refHeader);
            if (!refWcs.IsValid) return null; 

            double anchorRa = refWcs.RefRaDeg; 
            double anchorDec = refWcs.RefDecDeg;

            var header = await _fitsService.ReadHeaderOnlyAsync(_sourcePaths[index]);
            int height = header?.GetIntValue("NAXIS2") ?? 0;
            var wcs = WcsHeaderParser.Parse(header);
            
            if (wcs.IsValid) 
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
                var header0 = await _fitsService.ReadHeaderOnlyAsync(_sourcePaths[0]);
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