using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KomaLab.Models.Fits;
using KomaLab.Models.Primitives;
using KomaLab.Models.Processing;
using KomaLab.Services.Factories;
using KomaLab.Services.Fits;
using KomaLab.Services.Processing.Coordinators;
using KomaLab.ViewModels.Visualization;
using CoordinateEntry = KomaLab.ViewModels.Shared.CoordinateEntry;
using SequenceNavigator = KomaLab.ViewModels.Shared.SequenceNavigator;

namespace KomaLab.ViewModels.ImageProcessing;

/// <summary>
/// ViewModel per il Tool di Allineamento.
/// Gestisce la logica di analisi dei centroidi e warping delle immagini.
/// </summary>
public partial class AlignmentToolViewModel : ObservableObject, IDisposable
{
    private readonly IAlignmentCoordinator _coordinator;
    private readonly IFitsDataManager _dataManager;
    private readonly IFitsRendererFactory _rendererFactory;

    // --- Componenti ---
    public SequenceNavigator Navigator { get; } = new();
    public AlignmentImageViewport Viewport { get; } = new();
    public ObservableCollection<CoordinateEntry> CoordinateEntries { get; } = new();

    private readonly List<FitsFileReference> _files;
    private CancellationTokenSource? _navigationCts;

    // TCS richiesto dal code-behind per sincronizzare il primo caricamento
    public TaskCompletionSource<bool> ImageLoadedTcs { get; } = new();

    #region Stato UI e Binding

    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(IsCalculateButtonVisible), nameof(IsApplyCancelButtonsVisible), nameof(IsProcessingVisible), nameof(IsNavigationVisible))]
    private AlignmentState _currentState = AlignmentState.Initial;

    [ObservableProperty] private AlignmentStatus _status = AlignmentStatus.Idle;
    [ObservableProperty] private string _statusMessage = string.Empty;

    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(IsInteractionEnabled))]
    private bool _isBusy;

    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(IsTargetNameInputVisible), nameof(AstrometryOptionLabel))]
    private AlignmentTarget _selectedTarget = AlignmentTarget.Comet;

    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(IsNavigationVisible))]
    private AlignmentMode _selectedMode = AlignmentMode.Automatic;

    [ObservableProperty] private int _searchRadius = 100;
    [ObservableProperty] private bool _useJplAstrometry;
    [ObservableProperty] private string _targetName = string.Empty;

    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(SafeImage), nameof(BlackPoint), nameof(WhitePoint))]
    private FitsRenderer? _activeRenderer;

    // --- Proprietà richieste espressamente dallo XAML e dal Code-behind ---
    public Bitmap? SafeImage => ActiveRenderer?.Image;
    public bool IsInteractionEnabled => !IsBusy;
    public int MinSearchRadius => 5;
    public int MaxSearchRadius => 500;
    public AlignmentMode[] AvailableModes => Enum.GetValues<AlignmentMode>();
    
    // Contatore per la barra di navigazione
    public string CurrentImageText => $"{Navigator.DisplayIndex} / {Navigator.TotalCount}";

    // Proxy per soglie radiometriche (richiesti dai binding e code-behind)
    public double BlackPoint
    {
        get => ActiveRenderer?.BlackPoint ?? 0;
        set { if (ActiveRenderer != null) ActiveRenderer.BlackPoint = value; OnPropertyChanged(); }
    }

    public double WhitePoint
    {
        get => ActiveRenderer?.WhitePoint ?? 65535;
        set { if (ActiveRenderer != null) ActiveRenderer.WhitePoint = value; OnPropertyChanged(); }
    }

    public List<string>? FinalProcessedPaths { get; private set; }
    public bool DialogResult { get; private set; }
    public event Action? RequestClose;

    // Helpers per la visibilità dei controlli
    public bool IsCalculateButtonVisible => CurrentState == AlignmentState.Initial;
    public bool IsApplyCancelButtonsVisible => CurrentState == AlignmentState.ResultsReady;
    public bool IsProcessingVisible => CurrentState == AlignmentState.Processing || CurrentState == AlignmentState.Calculating;
    public bool IsTargetNameInputVisible => SelectedTarget == AlignmentTarget.Comet;
    public string AstrometryOptionLabel => SelectedTarget == AlignmentTarget.Stars ? "Usa WCS Header" : "Usa NASA/JPL";

    // Mostra il navigatore solo se non stiamo calcolando e se la modalità lo permette
    public bool IsNavigationVisible => CurrentState != AlignmentState.Calculating && 
                                       (CurrentState != AlignmentState.Initial || SelectedMode != AlignmentMode.Automatic);

    public string ProcessingStatusText => CurrentState switch
    {
        AlignmentState.Calculating => "Analisi centroidi...",
        AlignmentState.Processing => "Salvataggio immagini...",
        _ => "In corso..."
    };

    #endregion

    public AlignmentToolViewModel(
        List<string> sourcePaths,
        IAlignmentCoordinator coordinator,
        IFitsDataManager dataManager,
        IFitsRendererFactory rendererFactory)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _dataManager = dataManager ?? throw new ArgumentNullException(nameof(dataManager));
        _rendererFactory = rendererFactory ?? throw new ArgumentNullException(nameof(rendererFactory));

        _files = sourcePaths.Select(p => new FitsFileReference(p)).ToList();
        
        Navigator.UpdateStatus(0, _files.Count);
        Navigator.IndexChanged += OnNavigatorIndexChanged;

        _ = InitializeToolAsync();
    }

    private async Task InitializeToolAsync()
    {
        IsBusy = true;
        try
        {
            CoordinateEntries.Clear();
            foreach (var f in _files)
                CoordinateEntries.Add(new CoordinateEntry { DisplayName = System.IO.Path.GetFileName(f.FilePath) });

            var metadata = await _coordinator.GetFileMetadataAsync(_files[0]);
            TargetName = metadata.ObjectName;
            
            await LoadImageAsync(0);
            ImageLoadedTcs.TrySetResult(true);
        }
        catch 
        { 
            Status = AlignmentStatus.Error; 
            StatusMessage = "Errore caricamento dati iniziali.";
            ImageLoadedTcs.TrySetException(new Exception("Load failed"));
        }
        finally { IsBusy = false; }
    }

    private async void OnNavigatorIndexChanged(object? sender, int index)
    {
        OnPropertyChanged(nameof(CurrentImageText));
        await LoadImageAsync(index);
    }

    // ---------------------------------------------------------------------------
    // CORE RENDERING & VIEWPORT PROXIES
    // ---------------------------------------------------------------------------

    private async Task LoadImageAsync(int index)
    {
        if (index < 0 || index >= _files.Count) return;

        _navigationCts?.Cancel();
        _navigationCts?.Dispose();
        _navigationCts = new CancellationTokenSource();
        var token = _navigationCts.Token;

        try
        {
            var file = _files[index];
            var data = await _dataManager.GetDataAsync(file.FilePath);
            token.ThrowIfCancellationRequested();

            // 1. ASYNC FACTORY (Caricamento Atomico e Sicuro)
            var newRenderer = await _rendererFactory.CreateAsync(
                data.PixelData, 
                file.ModifiedHeader ?? data.Header
            );

            // 2. LOGICA ADATTIVA
            if (ActiveRenderer != null)
            {
                using var nextMat = newRenderer.CaptureScientificMat(); // Sicuro: già inizializzato
                token.ThrowIfCancellationRequested();
                var profile = ActiveRenderer.GetAdaptedProfileFor(nextMat);
                newRenderer.ApplyContrastProfile(profile);
                
                // Cleanup immediato per liberare RAM
                ActiveRenderer.Dispose();
            }

            // 3. SWAP
            ActiveRenderer = newRenderer;

            // Sincronizzazione Viewport e Binding
            Viewport.ImageSize = ActiveRenderer.ImageSize;
            Viewport.SearchRadius = SearchRadius; 
            Viewport.TargetCoordinate = CoordinateEntries[index].Coordinate;
            
            OnPropertyChanged(nameof(BlackPoint));
            OnPropertyChanged(nameof(WhitePoint));
            OnPropertyChanged(nameof(SafeImage));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status = AlignmentStatus.Error; StatusMessage = $"Errore rendering: {ex.Message}"; }
    }

    // Metodi proxy per il code-behind della View
    public void ApplyPan(double dx, double dy) => Viewport.ApplyPan(dx, dy);
    public void ApplyZoomAtPoint(double factor, Point pivot) => Viewport.ApplyZoomAtPoint(factor, pivot);

    #region Comandi

    [RelayCommand]
    private void ResetView() => Viewport.ResetView();

    [RelayCommand]
    private async Task ResetThresholds()
    {
        if (ActiveRenderer != null)
        {
            await ActiveRenderer.ResetThresholdsAsync();
            OnPropertyChanged(nameof(BlackPoint));
            OnPropertyChanged(nameof(WhitePoint));
        }
    }

    [RelayCommand(CanExecute = nameof(CanCalculate))]
    private async Task CalculateCenters()
    {
        CurrentState = AlignmentState.Calculating;
        Status = AlignmentStatus.Running;
        IsBusy = true;

        try
        {
            var guesses = CoordinateEntries.Select(e => e.Coordinate.HasValue 
                ? new Point2D(e.Coordinate.Value.X, e.Coordinate.Value.Y) 
                : (Point2D?)null).ToList();
            
            if (UseJplAstrometry)
            {
                guesses = await _coordinator.DiscoverStartingPointsAsync(_files, SelectedTarget, TargetName);
            }

            var progress = new Progress<AlignmentProgressReport>(p => {
                StatusMessage = p.Message;
                if (p.CurrentIndex <= CoordinateEntries.Count && p.FoundCenter.HasValue)
                {
                    CoordinateEntries[p.CurrentIndex - 1].Coordinate = new Point(p.FoundCenter.Value.X, p.FoundCenter.Value.Y);
                    
                    if (Navigator.CurrentIndex == p.CurrentIndex - 1)
                        Viewport.TargetCoordinate = CoordinateEntries[Navigator.CurrentIndex].Coordinate;
                }
            });

            await _coordinator.AnalyzeSequenceAsync(_files, guesses, SelectedTarget, SelectedMode, CenteringMethod.LocalRegion, SearchRadius, progress);

            CurrentState = AlignmentState.ResultsReady;
            Status = AlignmentStatus.Success;
            StatusMessage = "Analisi completata.";
        }
        catch (Exception ex) 
        { 
            StatusMessage = ex.Message; 
            Status = AlignmentStatus.Error;
            CurrentState = AlignmentState.Initial; 
        }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAlignment()
    {
        CurrentState = AlignmentState.Processing;
        Status = AlignmentStatus.Running;
        IsBusy = true;
        try
        {
            var centers = CoordinateEntries.Select(e => e.Coordinate.HasValue 
                ? new Point2D(e.Coordinate.Value.X, e.Coordinate.Value.Y) 
                : (Point2D?)null).ToList();

            var map = await _coordinator.AnalyzeSequenceAsync(_files, centers, SelectedTarget, SelectedMode, CenteringMethod.LocalRegion, 0);
            FinalProcessedPaths = await _coordinator.ExecuteWarpingAsync(_files, map);
            
            DialogResult = true;
            RequestClose?.Invoke();
        }
        catch 
        { 
            Status = AlignmentStatus.Error; 
            StatusMessage = "Errore durante il salvataggio."; 
            CurrentState = AlignmentState.Initial; 
        }
        finally { IsBusy = false; }
    }

    [RelayCommand] private void Cancel() => RequestClose?.Invoke();

    private bool CanCalculate() => !IsBusy && _files.Count > 0;
    private bool CanApply() => CurrentState == AlignmentState.ResultsReady && !IsBusy;

    #endregion

    #region Interazione Viewport

    public void SetTargetCoordinate(Point pt)
    {
        if (CurrentState != AlignmentState.Initial) return;
        
        if (SelectedMode == AlignmentMode.Guided && Navigator.CurrentIndex > 0 && Navigator.CurrentIndex < _files.Count - 1) 
            return;

        CoordinateEntries[Navigator.CurrentIndex].Coordinate = pt;
        Viewport.TargetCoordinate = pt;
        CalculateCentersCommand.NotifyCanExecuteChanged();
    }

    public void ClearTarget()
    {
        if (CurrentState != AlignmentState.Initial) { ResetToInitial(); return; }
        
        CoordinateEntries[Navigator.CurrentIndex].Coordinate = null;
        Viewport.TargetCoordinate = null;
        CalculateCentersCommand.NotifyCanExecuteChanged();
    }

    private void ResetToInitial()
    {
        CurrentState = AlignmentState.Initial;
        foreach (var e in CoordinateEntries) e.Coordinate = null;
        Viewport.TargetCoordinate = null;
        Status = AlignmentStatus.Idle;
        StatusMessage = string.Empty;
    }

    partial void OnSearchRadiusChanged(int value)
    {
        Viewport.SearchRadius = value;
    }

    #endregion

    public void Dispose()
    {
        Navigator.IndexChanged -= OnNavigatorIndexChanged;
        _navigationCts?.Cancel();
        _navigationCts?.Dispose();
        ActiveRenderer?.Dispose();
        GC.SuppressFinalize(this);
    }
}