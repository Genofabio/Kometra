using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
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

    private static readonly IBrush ColorNormal = Brushes.Cyan;
    private static readonly IBrush ColorSuccess = new SolidColorBrush(Color.Parse("#03A077"));
    private static readonly IBrush ColorError = new SolidColorBrush(Color.Parse("#E6606A"));
    private static readonly IBrush ColorLoading = new SolidColorBrush(Color.Parse("#8058E8"));

    #region Stato UI e Binding

    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(IsCalculateButtonVisible), nameof(IsApplyCancelButtonsVisible), nameof(IsProcessingVisible), nameof(IsNavigationVisible))]
    [NotifyPropertyChangedFor(nameof(IsSetupVisible), nameof(IsResultsListVisible), nameof(IsSetupControlsEnabled))]
    [NotifyPropertyChangedFor(nameof(IsTargetPlacementAllowed))]
    [NotifyPropertyChangedFor(nameof(ProcessingStatusText))]
    [NotifyCanExecuteChangedFor(nameof(ApplyAlignmentCommand))] 
    private AlignmentState _currentState = AlignmentState.Initial;

    [ObservableProperty] private AlignmentStatus _status = AlignmentStatus.Idle;
    [ObservableProperty] private string _statusMessage = string.Empty;

    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(IsInteractionEnabled), nameof(IsSetupControlsEnabled))]
    [NotifyCanExecuteChangedFor(nameof(CalculateCentersCommand))] 
    [NotifyCanExecuteChangedFor(nameof(ApplyAlignmentCommand))]
    private bool _isBusy;

    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(IsTargetNameInputVisible), nameof(AstrometryOptionLabel), nameof(IsVerifyButtonVisible), nameof(IsJplOptionVisible), nameof(AvailableModes), nameof(IsSearchRadiusVisible), nameof(CurrentImageText), nameof(IsTargetPlacementAllowed))]
    [NotifyCanExecuteChangedFor(nameof(CalculateCentersCommand))]
    private AlignmentTarget _selectedTarget = AlignmentTarget.Comet;

    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(IsNavigationVisible), nameof(IsJplOptionVisible), nameof(IsSearchRadiusVisible), nameof(CurrentImageText), nameof(IsTargetPlacementAllowed))]
    [NotifyCanExecuteChangedFor(nameof(CalculateCentersCommand))]
    private AlignmentMode _selectedMode = AlignmentMode.Automatic;

    [ObservableProperty] private int _searchRadius = 100;
    
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
    [ObservableProperty] private IBrush _astrometryStatusBrush = ColorNormal;

    // --- REGOLE DI BUSINESS ---
    public AlignmentMode[] AvailableModes => SelectedTarget == AlignmentTarget.Stars 
        ? new[] { AlignmentMode.Automatic } 
        : new[] { AlignmentMode.Automatic, AlignmentMode.Guided, AlignmentMode.Manual };

    public bool IsSearchRadiusVisible => SelectedTarget == AlignmentTarget.Comet && SelectedMode != AlignmentMode.Automatic;
    
    public bool IsJplOptionVisible => 
        (SelectedTarget == AlignmentTarget.Comet && SelectedMode != AlignmentMode.Manual) || 
        (SelectedTarget == AlignmentTarget.Stars);

    public bool IsTargetPlacementAllowed => 
        CurrentState == AlignmentState.ResultsReady || 
        (CurrentState == AlignmentState.Initial && SelectedTarget == AlignmentTarget.Comet && SelectedMode != AlignmentMode.Automatic);

    public bool IsSetupVisible => CurrentState == AlignmentState.Initial || CurrentState == AlignmentState.Calculating;
    public bool IsResultsListVisible => CurrentState == AlignmentState.ResultsReady || CurrentState == AlignmentState.Processing;
    
    public bool IsSetupControlsEnabled => CurrentState == AlignmentState.Initial && !IsBusy;

    public string CurrentImageText
    {
        get
        {
            if (CurrentState == AlignmentState.ResultsReady) 
                return $"{Navigator.DisplayIndex} / {Navigator.TotalCount}";

            if (SelectedTarget == AlignmentTarget.Stars || SelectedMode == AlignmentMode.Automatic) return "1 / 1";
            if (SelectedMode == AlignmentMode.Guided) return $"{(Navigator.CurrentIndex == 0 ? 1 : 2)} / 2";
            return $"{Navigator.DisplayIndex} / {Navigator.TotalCount}";
        }
    }

    public bool IsNavigationVisible => 
        CurrentState != AlignmentState.Calculating && 
        (
            CurrentState == AlignmentState.ResultsReady || 
            (CurrentState == AlignmentState.Initial && (SelectedTarget == AlignmentTarget.Comet && SelectedMode != AlignmentMode.Automatic))
        );

    public Bitmap? SafeImage => ActiveRenderer?.Image;
    public bool IsInteractionEnabled => !IsBusy && !IsProcessingVisible;
    public int MinSearchRadius => 5;
    public int MaxSearchRadius => 500;
    
    // FIX 2: Il pulsante è visibile SOLO nello stato iniziale. Appena inizia il calcolo, scompare.
    public bool IsCalculateButtonVisible => CurrentState == AlignmentState.Initial;
    
    public bool IsApplyCancelButtonsVisible => CurrentState == AlignmentState.ResultsReady;
    public bool IsProcessingVisible => CurrentState == AlignmentState.Processing || CurrentState == AlignmentState.Calculating;
    public bool IsTargetNameInputVisible => SelectedTarget == AlignmentTarget.Comet;
    public bool IsVerifyButtonVisible => SelectedTarget == AlignmentTarget.Comet;
    public string AstrometryOptionLabel => SelectedTarget == AlignmentTarget.Stars ? "Usa WCS Header" : "Usa NASA/JPL";

    public string ProcessingStatusText 
    {
        get
        {
            if (CurrentState == AlignmentState.Processing) return "Salvataggio immagini...";
            
            if (CurrentState == AlignmentState.Calculating)
            {
                if (UseJplAstrometry)
                {
                    return SelectedTarget == AlignmentTarget.Stars 
                        ? "Risoluzione Astrometrica (WCS)..." 
                        : "Scaricamento Efemeridi JPL...";
                }
                return "Rilevamento Centroidi...";
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
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _dataManager = dataManager ?? throw new ArgumentNullException(nameof(dataManager));
        _rendererFactory = rendererFactory ?? throw new ArgumentNullException(nameof(rendererFactory));

        _files = sourcePaths.Select(p => new FitsFileReference(p)).ToList();
        
        Navigator.UpdateStatus(0, _files.Count);
        Navigator.IndexFilter = IsIndexAccessible; 
        Navigator.IndexChanged += OnNavigatorIndexChanged;

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
                CoordinateEntries.Add(new CoordinateEntry 
                { 
                    Index = i, 
                    DisplayName = System.IO.Path.GetFileName(_files[i].FilePath) 
                });
            }

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

    partial void OnSelectedTargetChanged(AlignmentTarget value) => ResetAlignmentState();
    partial void OnSelectedModeChanged(AlignmentMode value) => ResetAlignmentState();

    private bool IsIndexAccessible(int index)
    {
        if (CurrentState == AlignmentState.ResultsReady) return true;

        if (index < 0 || index >= _files.Count) return false;
        if (SelectedTarget == AlignmentTarget.Stars || SelectedMode == AlignmentMode.Automatic) return index == 0;
        if (SelectedMode == AlignmentMode.Guided) return index == 0 || index == _files.Count - 1;
        return true;
    }

    private void ResetAlignmentState()
    {
        CurrentState = AlignmentState.Initial;
        foreach (var e in CoordinateEntries) e.Coordinate = null;
        Viewport.TargetCoordinate = null;
        StatusMessage = string.Empty;

        Navigator.MoveTo(0);
        Navigator.RefreshState(); 
        
        CalculateCentersCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CurrentImageText));
    }

    [RelayCommand(CanExecute = nameof(CanVerifyJpl))]
    private async Task VerifyJpl()
    {
        IsVerifyingJpl = true;
        AstrometryStatusBrush = ColorLoading;
        AstrometryStatusMessage = "Verifica in corso...";

        try
        {
            var testPoints = await _coordinator.DiscoverStartingPointsAsync(_files.Take(1), SelectedTarget, TargetName);
            if (testPoints.Any(p => p.HasValue))
            {
                AstrometryStatusMessage = SelectedTarget == AlignmentTarget.Stars ? "WCS Valido." : "Dati JPL trovati.";
                AstrometryStatusBrush = ColorSuccess;
            }
            else
            {
                AstrometryStatusMessage = "Dati non trovati.";
                AstrometryStatusBrush = ColorError;
            }
        }
        catch (Exception ex)
        {
            AstrometryStatusMessage = "Errore: " + ex.Message;
            AstrometryStatusBrush = ColorError;
        }
        finally
        {
            IsVerifyingJpl = false;
            CalculateCentersCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanVerifyJpl() => !IsVerifyingJpl && !string.IsNullOrWhiteSpace(TargetName);

    private async void OnNavigatorIndexChanged(object? sender, int index)
    {
        OnPropertyChanged(nameof(CurrentImageText));
        await LoadImageAsync(index);
    }

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

            var newRenderer = await _rendererFactory.CreateAsync(data.PixelData, file.ModifiedHeader ?? data.Header);
            token.ThrowIfCancellationRequested();

            if (ActiveRenderer != null)
            {
                using var nextMat = newRenderer.CaptureScientificMat();
                var profile = ActiveRenderer.GetAdaptedProfileFor(nextMat);
                newRenderer.ApplyContrastProfile(profile);
                ActiveRenderer.Dispose();
            }

            ActiveRenderer = newRenderer;
            Viewport.ImageSize = ActiveRenderer.ImageSize;
            Viewport.SearchRadius = SearchRadius; 
            
            Viewport.TargetCoordinate = CoordinateEntries[index].Coordinate;

            if (Viewport.ViewportSize.Width > 0)
            {
                Viewport.ResetView();
            }
            
            OnPropertyChanged(nameof(BlackPoint));
            OnPropertyChanged(nameof(WhitePoint));
            OnPropertyChanged(nameof(SafeImage));

            for (int i = 0; i < CoordinateEntries.Count; i++)
            {
                CoordinateEntries[i].IsActive = (i == index);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) 
        { 
            StatusMessage = $"Errore rendering: {ex.Message}"; 
        }
    }

    #region Comandi core

    [RelayCommand] private void ResetView() => Viewport.ResetView();

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
        // 1. Blocco UI immediato
        CurrentState = AlignmentState.Calculating;
        IsBusy = true; 

        StatusMessage = "Inizializzazione calcolo...";

        try
        {
            List<Point2D?> guesses;

            if (UseJplAstrometry)
            {
                StatusMessage = SelectedTarget == AlignmentTarget.Stars ? "Analisi WCS..." : "Scaricamento dati JPL...";
                guesses = await _coordinator.DiscoverStartingPointsAsync(_files, SelectedTarget, TargetName);
            }
            else
            {
                if (SelectedTarget == AlignmentTarget.Comet && SelectedMode == AlignmentMode.Automatic)
                {
                    guesses = new List<Point2D?>(new Point2D?[_files.Count]);
                }
                else
                {
                    guesses = CoordinateEntries.Select(e => e.Coordinate.HasValue 
                        ? new Point2D(e.Coordinate.Value.X, e.Coordinate.Value.Y) 
                        : (Point2D?)null).ToList();
                }
            }

            var progress = new Progress<AlignmentProgressReport>(p => {
                StatusMessage = p.Message;
                if (p.CurrentIndex > 0 && p.CurrentIndex <= CoordinateEntries.Count && p.FoundCenter.HasValue)
                {
                    var pt = new Point(p.FoundCenter.Value.X, p.FoundCenter.Value.Y);
                    CoordinateEntries[p.CurrentIndex - 1].Coordinate = pt;
                }
            });

            await _coordinator.AnalyzeSequenceAsync(
                _files, 
                guesses, 
                SelectedTarget, 
                SelectedMode, 
                CenteringMethod.LocalRegion, 
                SearchRadius, 
                progress);

            CurrentState = AlignmentState.ResultsReady;
            StatusMessage = "Analisi completata. Verifica i risultati.";
            
            Navigator.RefreshState();
            OnPropertyChanged(nameof(CurrentImageText));
            OnPropertyChanged(nameof(IsNavigationVisible));

            Viewport.TargetCoordinate = CoordinateEntries[Navigator.CurrentIndex].Coordinate;
            ApplyAlignmentCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex) 
        { 
            StatusMessage = $"Errore: {ex.Message}"; 
            CurrentState = AlignmentState.Initial; 
        }
        finally 
        { 
            IsBusy = false; 
        }
    }

    private bool CanCalculate()
    {
        if (IsBusy || IsVerifyingJpl) return false;
        
        if (UseJplAstrometry && SelectedTarget == AlignmentTarget.Comet && string.IsNullOrWhiteSpace(TargetName))
            return false;

        if (SelectedTarget == AlignmentTarget.Stars || SelectedMode == AlignmentMode.Automatic) return true;
        
        if (SelectedMode == AlignmentMode.Guided) 
            return CoordinateEntries.Count > 0 && CoordinateEntries[0].Coordinate != null && CoordinateEntries[^1].Coordinate != null;
        
        if (SelectedMode == AlignmentMode.Manual) 
            return CoordinateEntries.All(e => e.Coordinate != null);
            
        return false;
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAlignment()
    {
        CurrentState = AlignmentState.Processing;
        IsBusy = true;

        try
        {
            var centers = CoordinateEntries.Select(e => e.Coordinate.HasValue ? new Point2D(e.Coordinate.Value.X, e.Coordinate.Value.Y) : (Point2D?)null).ToList();
            var map = await _coordinator.AnalyzeSequenceAsync(_files, centers, SelectedTarget, SelectedMode, CenteringMethod.LocalRegion, 0);
            FinalProcessedPaths = await _coordinator.ExecuteWarpingAsync(_files, map);
            DialogResult = true;
            RequestClose?.Invoke();
        }
        catch { StatusMessage = "Errore durante il salvataggio."; CurrentState = AlignmentState.Initial; }
        finally { IsBusy = false; }
    }

    // FIX 1: Cancel Button Logic
    [RelayCommand]
    private void Cancel()
    {
        if (CurrentState == AlignmentState.ResultsReady)
        {
            // Se siamo nella schermata risultati, torniamo alla configurazione iniziale
            ResetAlignmentState();
        }
        else
        {
            // Se siamo già all'inizio (o altro stato non gestito), chiudiamo la finestra
            RequestClose?.Invoke();
        }
    }
    
    private bool CanApply() => CurrentState == AlignmentState.ResultsReady && !IsBusy;
    
    [RelayCommand]
    private Task SelectImage(CoordinateEntry entry)
    {
        if (entry == null) return Task.CompletedTask;
    
        // Se siamo già su questa immagine, non facciamo nulla
        if (Navigator.CurrentIndex == entry.Index) return Task.CompletedTask;
        
        Navigator.MoveTo(entry.Index);
        return Task.CompletedTask;
    }

    #endregion

    #region Interazione Viewport

    public void SetTargetCoordinate(Point pt)
    {
        if (!IsTargetPlacementAllowed) return;
        CoordinateEntries[Navigator.CurrentIndex].Coordinate = pt;
        Viewport.TargetCoordinate = pt;
        CalculateCentersCommand.NotifyCanExecuteChanged();
    }

    public void ClearTarget()
    {
        if (CurrentState != AlignmentState.Initial && CurrentState != AlignmentState.ResultsReady) return;
        
        if (CurrentState == AlignmentState.Initial) 
        { 
            ResetAlignmentState(); 
            return; 
        }
        
        CoordinateEntries[Navigator.CurrentIndex].Coordinate = null;
        Viewport.TargetCoordinate = null;
    }

    partial void OnSearchRadiusChanged(int value) => Viewport.SearchRadius = value;
    
    partial void OnUseJplAstrometryChanged(bool value)
    {
        if (value) _ = VerifyJpl();
        else { AstrometryStatusMessage = ""; AstrometryStatusBrush = ColorNormal; }
        
        CalculateCentersCommand.NotifyCanExecuteChanged();
    }

    partial void OnTargetNameChanged(string value)
    {
        VerifyJplCommand.NotifyCanExecuteChanged();
        CalculateCentersCommand.NotifyCanExecuteChanged();
    }

    #endregion

    public void ApplyPan(double dx, double dy) => Viewport.ApplyPan(dx, dy);
    public void ApplyZoomAtPoint(double factor, Point pivot) => Viewport.ApplyZoomAtPoint(factor, pivot);

    public void Dispose()
    {
        Navigator.IndexChanged -= OnNavigatorIndexChanged;
        _navigationCts?.Cancel();
        _navigationCts?.Dispose();
        ActiveRenderer?.Dispose();
        GC.SuppressFinalize(this);
    }
}