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

public partial class AlignmentToolViewModel : ObservableObject, IDisposable
{
    private readonly IAlignmentCoordinator _coordinator;
    private readonly IFitsDataManager _dataManager;
    private readonly IFitsRendererFactory _rendererFactory;

    public SequenceNavigator Navigator { get; } = new();
    public AlignmentImageViewport Viewport { get; } = new();
    public ObservableCollection<CoordinateEntry> CoordinateEntries { get; } = new();

    private readonly List<FitsFileReference> _files;
    private CancellationTokenSource? _navigationCts;
    public TaskCompletionSource<bool> ImageLoadedTcs { get; } = new();

    #region Stato UI e Binding

    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(IsCalculateButtonVisible), nameof(IsApplyCancelButtonsVisible), nameof(IsProcessingVisible), nameof(IsNavigationVisible))]
    [NotifyPropertyChangedFor(nameof(IsSetupVisible), nameof(IsResultsListVisible), nameof(IsSetupControlsEnabled))]
    [NotifyPropertyChangedFor(nameof(IsTargetPlacementAllowed))]
    [NotifyPropertyChangedFor(nameof(ProcessingStatusText))]
    [NotifyPropertyChangedFor(nameof(IsSearchBoxOverlayVisible))]
    [NotifyPropertyChangedFor(nameof(CurrentImageText))]
    [NotifyCanExecuteChangedFor(nameof(ApplyAlignmentCommand))] 
    private AlignmentState _currentState = AlignmentState.Initial;
    
    [ObservableProperty] private string _statusMessage = string.Empty;

    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(IsInteractionEnabled), nameof(IsSetupControlsEnabled))]
    [NotifyCanExecuteChangedFor(nameof(CalculateCentersCommand))] 
    [NotifyCanExecuteChangedFor(nameof(ApplyAlignmentCommand))]
    private bool _isBusy;

    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(IsTargetNameInputVisible), nameof(AstrometryOptionLabel), nameof(IsVerifyButtonVisible), nameof(IsJplOptionVisible), nameof(AvailableModes), nameof(IsSearchRadiusVisible), nameof(CurrentImageText), nameof(IsTargetPlacementAllowed))]
    [NotifyPropertyChangedFor(nameof(CalculateButtonText))]
    [NotifyPropertyChangedFor(nameof(IsSearchBoxOverlayVisible))]
    [NotifyPropertyChangedFor(nameof(IsNavigationVisible))]
    [NotifyCanExecuteChangedFor(nameof(CalculateCentersCommand))]
    private AlignmentTarget _selectedTarget = AlignmentTarget.Comet;

    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(IsNavigationVisible), nameof(IsJplOptionVisible), nameof(IsSearchRadiusVisible), nameof(CurrentImageText), nameof(IsTargetPlacementAllowed))]
    [NotifyPropertyChangedFor(nameof(IsSearchBoxOverlayVisible))]
    [NotifyCanExecuteChangedFor(nameof(CalculateCentersCommand))]
    private AlignmentMode _selectedMode = AlignmentMode.Automatic;

    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(IsSearchBoxOverlayVisible))]
    private int _searchRadius = 100;
    
    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(SafeImage), nameof(BlackPoint), nameof(WhitePoint))]
    private FitsRenderer? _activeRenderer;

    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(ProcessingStatusText))]
    [NotifyCanExecuteChangedFor(nameof(CalculateCentersCommand))]
    private bool _useJplAstrometry;

    [ObservableProperty] 
    [NotifyCanExecuteChangedFor(nameof(VerifyJplCommand))]
    [NotifyCanExecuteChangedFor(nameof(CalculateCentersCommand))] 
    private string _targetName = string.Empty;
    
    [ObservableProperty] 
    [NotifyCanExecuteChangedFor(nameof(VerifyJplCommand))]
    [NotifyCanExecuteChangedFor(nameof(CalculateCentersCommand))]
    private bool _isVerifyingJpl;

    [ObservableProperty] private string _astrometryStatusMessage = string.Empty;
    
    [ObservableProperty] private JplStatus _astrometryStatus = JplStatus.Idle;

    // --- REGOLE DI BUSINESS ---
    public int MinSearchRadius => 5;
    public int MaxSearchRadius => 500;
    
    public bool IsMultipleImages => _files.Count > 1;

    public AlignmentMode[] AvailableModes
    {
        get
        {
            // Per 1 solo file, Guided non ha senso matematico
            if (_files.Count <= 1) return new[] { AlignmentMode.Automatic, AlignmentMode.Manual };

            if (SelectedTarget == AlignmentTarget.Stars)
                return new[] { AlignmentMode.Automatic };

            return new[] { AlignmentMode.Automatic, AlignmentMode.Guided, AlignmentMode.Manual };
        }
    }

    public bool IsSearchRadiusVisible => SelectedTarget == AlignmentTarget.Comet && SelectedMode != AlignmentMode.Automatic;
    
    public bool IsJplOptionVisible => 
        (SelectedTarget == AlignmentTarget.Comet && SelectedMode != AlignmentMode.Manual) || 
        (SelectedTarget == AlignmentTarget.Stars && _files.Count > 1);

    public bool IsTargetPlacementAllowed => 
        CurrentState == AlignmentState.ResultsReady || 
        (CurrentState == AlignmentState.Initial && SelectedTarget == AlignmentTarget.Comet && SelectedMode != AlignmentMode.Automatic);

    public bool IsSetupVisible => CurrentState == AlignmentState.Initial || CurrentState == AlignmentState.Calculating;
    public bool IsResultsListVisible => CurrentState == AlignmentState.ResultsReady || CurrentState == AlignmentState.Processing;
    public bool IsSetupControlsEnabled => CurrentState == AlignmentState.Initial && !IsBusy;

    public bool IsSearchBoxOverlayVisible => 
        CurrentState == AlignmentState.Initial && 
        IsSearchRadiusVisible && 
        Viewport.TargetCoordinate.HasValue;

    public string CurrentImageText => $"{Navigator.DisplayIndex} / {Navigator.TotalCount}";

    public bool IsNavigationVisible => 
        _files.Count > 1 && 
        CurrentState != AlignmentState.Calculating && 
        (CurrentState == AlignmentState.ResultsReady || (CurrentState == AlignmentState.Initial && SelectedTarget == AlignmentTarget.Comet && SelectedMode != AlignmentMode.Automatic));

    public Bitmap? SafeImage => ActiveRenderer?.Image;
    public bool IsInteractionEnabled => !IsBusy && !IsProcessingVisible;
    public bool IsCalculateButtonVisible => CurrentState == AlignmentState.Initial;
    public bool IsApplyCancelButtonsVisible => CurrentState == AlignmentState.ResultsReady;
    public bool IsProcessingVisible => CurrentState == AlignmentState.Processing || CurrentState == AlignmentState.Calculating;
    
    // Rimosso il vincolo _files.Count > 1 per permettere l'uso di JPL anche su scatto singolo
    public bool IsTargetNameInputVisible => SelectedTarget == AlignmentTarget.Comet;
    public bool IsVerifyButtonVisible => SelectedTarget == AlignmentTarget.Comet;
    
    public string AstrometryOptionLabel => SelectedTarget == AlignmentTarget.Stars ? "Usa WCS Header" : "Usa NASA/JPL";
    public string CalculateButtonText => SelectedTarget == AlignmentTarget.Stars ? "Allinea Stelle" : "Calcola Centri";

    public string ProcessingStatusText 
    {
        get
        {
            if (CurrentState == AlignmentState.Processing) return "Salvataggio immagini...";
            if (CurrentState == AlignmentState.Calculating)
            {
                return UseJplAstrometry 
                    ? (SelectedTarget == AlignmentTarget.Stars ? "Risoluzione WCS..." : "Dati JPL/NASA...") 
                    : "Analisi centratura...";
            }
            return "Elaborazione in corso...";
        }
    }

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

    #endregion

    public AlignmentToolViewModel(
        List<string> sourcePaths,
        IAlignmentCoordinator coordinator,
        IFitsDataManager dataManager,
        IFitsRendererFactory rendererFactory)
    {
        _coordinator = coordinator;
        _dataManager = dataManager;
        _rendererFactory = rendererFactory;
        _files = sourcePaths.Select(p => new FitsFileReference(p)).ToList();
        
        Navigator.IndexFilter = IsIndexAccessible; 
        Navigator.IndexChanged += OnNavigatorIndexChanged;
        
        UpdateNavigationStatus(); 

        _ = InitializeToolAsync();
    }

    private async Task InitializeToolAsync()
    {
        IsBusy = true;
        try
        {
            CoordinateEntries.Clear();
            for (int i = 0; i < _files.Count; i++)
            {
                CoordinateEntries.Add(new CoordinateEntry { Index = i, DisplayName = System.IO.Path.GetFileName(_files[i].FilePath) });
            }

            var metadata = await _coordinator.GetFileMetadataAsync(_files[0]);
            TargetName = metadata.ObjectName;
            
            // DEFAULT FIX: Predefinito sempre su Automatico
            SelectedTarget = AlignmentTarget.Comet;
            SelectedMode = AlignmentMode.Automatic;

            await LoadImageAsync(0);
            ImageLoadedTcs.TrySetResult(true);
        }
        catch 
        { 
            StatusMessage = "Errore caricamento iniziale.";
            ImageLoadedTcs.TrySetException(new Exception("Load failed"));
        }
        finally { IsBusy = false; }
    }

    private void UpdateNavigationStatus()
    {
        Navigator.UpdateStatus(Navigator.CurrentIndex, _files.Count);
        OnPropertyChanged(nameof(CurrentImageText));
    }

    #region Gestione Cambi Mode/Target

    partial void OnSelectedTargetChanged(AlignmentTarget value)
    {
        if (_files.Count <= 1 && value == AlignmentTarget.Stars)
        {
            SelectedTarget = AlignmentTarget.Comet;
            return;
        }

        if (value == AlignmentTarget.Comet) SelectedMode = AlignmentMode.Automatic;
        ResetAlignmentState();
        if (UseJplAstrometry && SelectedMode != AlignmentMode.Manual) _ = VerifyJpl();
    }

    partial void OnSelectedModeChanged(AlignmentMode value)
    {
        ResetAlignmentState();
        if (value == AlignmentMode.Manual)
        {
            AstrometryStatus = JplStatus.Idle;
            AstrometryStatusMessage = string.Empty;
        }
        else if (UseJplAstrometry)
        {
            _ = VerifyJpl();
        }
        
        UpdateNavigationStatus();
    }

    private void ResetAlignmentState()
    {
        CurrentState = AlignmentState.Initial;
        ClearUiCoordinates();
        StatusMessage = string.Empty;
        
        if (SelectedMode == AlignmentMode.Manual || !UseJplAstrometry)
        {
            AstrometryStatus = JplStatus.Idle;
            AstrometryStatusMessage = string.Empty;
        }

        Navigator.MoveTo(0);
        UpdateNavigationStatus();
        CalculateCentersCommand.NotifyCanExecuteChanged();
    }

    #endregion

    #region Comando JPL (Astrometria)

    [RelayCommand(CanExecute = nameof(CanVerifyJpl))]
    private async Task VerifyJpl()
    {
        if (SelectedMode == AlignmentMode.Manual) return;

        IsVerifyingJpl = true;
        AstrometryStatus = JplStatus.Verifying;
        AstrometryStatusMessage = "Verifica disponibilità dati...";

        try
        {
            var points = await _coordinator.DiscoverStartingPointsAsync(_files, SelectedTarget, TargetName);
            bool anyFound = points.Any(p => p.HasValue);

            if (anyFound)
            {
                if (SelectedMode == AlignmentMode.Guided) ApplyCoordinatesToUi(points);
                else ClearUiCoordinates(); 

                AstrometryStatusMessage = SelectedTarget == AlignmentTarget.Stars ? "WCS Valido." : "Dati JPL disponibili.";
                AstrometryStatus = JplStatus.Success;
            }
            else
            {
                AstrometryStatusMessage = "Dati non trovati.";
                AstrometryStatus = JplStatus.Error;
            }
        }
        catch (Exception ex)
        {
            AstrometryStatusMessage = $"Errore: {ex.Message}";
            AstrometryStatus = JplStatus.Error;
        }
        finally
        {
            IsVerifyingJpl = false;
            CalculateCentersCommand.NotifyCanExecuteChanged();
        }
    }

    partial void OnUseJplAstrometryChanged(bool value)
    {
        if (value && SelectedMode != AlignmentMode.Manual) _ = VerifyJpl();
        else { AstrometryStatusMessage = ""; AstrometryStatus = JplStatus.Idle; }
        CalculateCentersCommand.NotifyCanExecuteChanged();
    }

    private bool CanVerifyJpl() => !IsVerifyingJpl && !string.IsNullOrWhiteSpace(TargetName) && SelectedMode != AlignmentMode.Manual;

    #endregion

    #region Comandi Core

    [RelayCommand(CanExecute = nameof(CanCalculate))]
    private async Task CalculateCenters()
    {
        CurrentState = AlignmentState.Calculating;
        IsBusy = true;
        StatusMessage = "Avvio analisi...";

        try
        {
            var uiGuesses = GetCoordinatesFromUi();
            var progress = new Progress<AlignmentProgressReport>(p => {
                StatusMessage = p.Message;
                if (p.CurrentIndex > 0 && p.FoundCenter.HasValue)
                    UpdateSingleUiCoordinate(p.CurrentIndex - 1, p.FoundCenter.Value);
            });

            var resultMap = await _coordinator.AnalyzeSequenceAsync(
                files: _files, 
                guesses: uiGuesses, 
                target: SelectedTarget, 
                mode: SelectedMode, 
                method: CenteringMethod.LocalRegion, 
                searchRadius: SearchRadius, 
                jplTargetName: (UseJplAstrometry && SelectedMode != AlignmentMode.Manual) ? TargetName : null, 
                progress: progress);

            ApplyCoordinatesToUi(resultMap.Centers);
            
            CurrentState = AlignmentState.ResultsReady;
            StatusMessage = "Analisi completata. Verifica i risultati.";
            
            UpdateNavigationStatus(); 
            ApplyAlignmentCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex) 
        { 
            StatusMessage = $"Errore: {ex.Message}"; 
            CurrentState = AlignmentState.Initial; 
        }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAlignment()
    {
        CurrentState = AlignmentState.Processing;
        IsBusy = true;
        try
        {
            var centers = GetCoordinatesFromUi();
            var map = await _coordinator.AnalyzeSequenceAsync(
                files: _files, 
                guesses: centers, 
                target: SelectedTarget, 
                mode: SelectedMode, 
                method: CenteringMethod.LocalRegion, 
                searchRadius: 0,
                jplTargetName: (UseJplAstrometry && SelectedMode != AlignmentMode.Manual) ? TargetName : null);

            FinalProcessedPaths = await _coordinator.ExecuteWarpingAsync(_files, map);
            DialogResult = true;
            RequestClose?.Invoke();
        }
        catch { StatusMessage = "Errore durante il salvataggio."; CurrentState = AlignmentState.Initial; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void Cancel()
    {
        if (CurrentState == AlignmentState.ResultsReady) ResetAlignmentState();
        else RequestClose?.Invoke();
    }

    [RelayCommand]
    private async Task SelectImage(CoordinateEntry entry)
    {
        if (entry == null || Navigator.CurrentIndex == entry.Index) return;
        Navigator.MoveTo(entry.Index);
        await Task.CompletedTask;
    }

    private bool CanCalculate()
    {
        if (IsBusy || IsVerifyingJpl) return false;
        
        if (SelectedMode == AlignmentMode.Manual) 
            return CoordinateEntries.All(e => e.Coordinate != null);

        if (UseJplAstrometry && SelectedTarget == AlignmentTarget.Comet && string.IsNullOrWhiteSpace(TargetName)) return false;
        if (SelectedTarget == AlignmentTarget.Stars || SelectedMode == AlignmentMode.Automatic) return true;
        if (SelectedMode == AlignmentMode.Guided) return CoordinateEntries[0].Coordinate != null && CoordinateEntries[^1].Coordinate != null;
        
        return false;
    }

    private bool CanApply() => CurrentState == AlignmentState.ResultsReady && !IsBusy;

    #endregion

    #region Helper Sincronizzazione

    private List<Point2D?> GetCoordinatesFromUi() => 
        CoordinateEntries.Select(e => e.Coordinate.HasValue ? new Point2D(e.Coordinate.Value.X, e.Coordinate.Value.Y) : (Point2D?)null).ToList();

    private void ApplyCoordinatesToUi(IEnumerable<Point2D?> points)
    {
        var list = points.ToList();
        for (int i = 0; i < list.Count; i++)
        {
            if (i < CoordinateEntries.Count) UpdateSingleUiCoordinate(i, list[i]);
        }
        Viewport.TargetCoordinate = CoordinateEntries[Navigator.CurrentIndex].Coordinate;
        OnPropertyChanged(nameof(IsSearchBoxOverlayVisible));
    }

    private void UpdateSingleUiCoordinate(int index, Point2D? pt)
    {
        CoordinateEntries[index].Coordinate = pt.HasValue ? new Point(pt.Value.X, pt.Value.Y) : null;
    }

    private void ClearUiCoordinates()
    {
        foreach (var e in CoordinateEntries) e.Coordinate = null;
        Viewport.TargetCoordinate = null;
        OnPropertyChanged(nameof(IsSearchBoxOverlayVisible));
    }

    #endregion

    #region Navigazione e Rendering

    private bool IsIndexAccessible(int index)
    {
        if (CurrentState == AlignmentState.ResultsReady) return true;
        if (index < 0 || index >= _files.Count) return false;
        if (_files.Count <= 1) return index == 0;

        if (SelectedTarget == AlignmentTarget.Stars || SelectedMode == AlignmentMode.Automatic) return index == 0;
        if (SelectedMode == AlignmentMode.Guided) return index == 0 || index == _files.Count - 1;
        return true;
    }

    private async void OnNavigatorIndexChanged(object? sender, int index)
    {
        UpdateNavigationStatus();
        await LoadImageAsync(index);
    }

    private async Task LoadImageAsync(int index)
    {
        if (index < 0 || index >= _files.Count) return;
        _navigationCts?.Cancel();
        _navigationCts = new CancellationTokenSource();
        var token = _navigationCts.Token;

        try
        {
            var file = _files[index];
            var data = await _dataManager.GetDataAsync(file.FilePath);
            token.ThrowIfCancellationRequested();

            var newRenderer = await _rendererFactory.CreateAsync(data.PixelData, file.ModifiedHeader ?? data.Header);
            token.ThrowIfCancellationRequested();

            if (ActiveRenderer != null)
            {
                using var nextMat = newRenderer.CaptureScientificMat();
                newRenderer.ApplyContrastProfile(ActiveRenderer.GetAdaptedProfileFor(nextMat));
                ActiveRenderer.Dispose();
            }

            ActiveRenderer = newRenderer;
            Viewport.ImageSize = ActiveRenderer.ImageSize;
            Viewport.SearchRadius = SearchRadius; 

            if (CoordinateEntries.Count > 0 && CoordinateEntries[0].ImageHeight == 0)
            {
                foreach (var entry in CoordinateEntries) entry.ImageHeight = ActiveRenderer.ImageSize.Height;
            }

            Viewport.TargetCoordinate = CoordinateEntries[index].Coordinate;
            OnPropertyChanged(nameof(SafeImage));
            OnPropertyChanged(nameof(IsSearchBoxOverlayVisible));
        }
        catch (OperationCanceledException) { }
        catch { StatusMessage = "Errore rendering immagine."; }
    }

    #endregion

    #region Interazione Viewport

    public void SetTargetCoordinate(Point pt)
    {
        if (!IsTargetPlacementAllowed || ActiveRenderer == null) return;
        var clamped = new Point(Math.Clamp(pt.X, 0, ActiveRenderer.ImageSize.Width), Math.Clamp(pt.Y, 0, ActiveRenderer.ImageSize.Height));
        CoordinateEntries[Navigator.CurrentIndex].Coordinate = clamped;
        Viewport.TargetCoordinate = clamped;
        
        OnPropertyChanged(nameof(IsSearchBoxOverlayVisible));
        CalculateCentersCommand.NotifyCanExecuteChanged(); 
    }

    public void ClearTarget()
    {
        if (CurrentState == AlignmentState.Initial) ResetAlignmentState();
        else 
        { 
            CoordinateEntries[Navigator.CurrentIndex].Coordinate = null; 
            Viewport.TargetCoordinate = null; 
            OnPropertyChanged(nameof(IsSearchBoxOverlayVisible));
            CalculateCentersCommand.NotifyCanExecuteChanged(); 
        }
    }

    [RelayCommand] private void ResetView() => Viewport.ResetView();
    [RelayCommand] private async Task ResetThresholds() { if (ActiveRenderer != null) { await ActiveRenderer.ResetThresholdsAsync(); OnPropertyChanged(nameof(BlackPoint)); OnPropertyChanged(nameof(WhitePoint)); } }
    
    partial void OnSearchRadiusChanged(int value) => Viewport.SearchRadius = value;
    partial void OnTargetNameChanged(string value) { VerifyJplCommand.NotifyCanExecuteChanged(); CalculateCentersCommand.NotifyCanExecuteChanged(); }

    #endregion

    public void ApplyPan(double dx, double dy) => Viewport.ApplyPan(dx, dy);
    public void ApplyZoomAtPoint(double factor, Point pivot) => Viewport.ApplyZoomAtPoint(factor, pivot);

    public void Dispose()
    {
        Navigator.IndexChanged -= OnNavigatorIndexChanged;
        _navigationCts?.Cancel();
        ActiveRenderer?.Dispose();
    }
}