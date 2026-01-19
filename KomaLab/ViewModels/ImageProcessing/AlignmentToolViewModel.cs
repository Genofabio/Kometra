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
    [NotifyPropertyChangedFor(nameof(IsCalculateButtonVisible), nameof(IsApplyCancelButtonsVisible), nameof(IsProcessingVisible), nameof(IsNavigationVisible), nameof(IsJplOptionVisible))]
    private AlignmentState _currentState = AlignmentState.Initial;

    [ObservableProperty] private AlignmentStatus _status = AlignmentStatus.Idle;
    [ObservableProperty] private string _statusMessage = string.Empty;

    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(IsInteractionEnabled))]
    private bool _isBusy;

    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(IsTargetNameInputVisible), nameof(AstrometryOptionLabel), nameof(IsVerifyButtonVisible), nameof(IsJplOptionVisible), nameof(AvailableModes), nameof(IsSearchRadiusVisible), nameof(CurrentImageText), nameof(IsTargetPlacementAllowed))]
    private AlignmentTarget _selectedTarget = AlignmentTarget.Comet;

    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(IsNavigationVisible), nameof(IsJplOptionVisible), nameof(IsSearchRadiusVisible), nameof(CurrentImageText), nameof(IsTargetPlacementAllowed))]
    private AlignmentMode _selectedMode = AlignmentMode.Automatic;

    [ObservableProperty] private int _searchRadius = 100;
    
    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(SafeImage), nameof(BlackPoint), nameof(WhitePoint))]
    private FitsRenderer? _activeRenderer;

    [ObservableProperty] private bool _useJplAstrometry;
    [ObservableProperty] private string _targetName = string.Empty;
    
    [ObservableProperty] 
    [NotifyCanExecuteChangedFor(nameof(VerifyJplCommand))]
    private bool _isVerifyingJpl;

    [ObservableProperty] private string _astrometryStatusMessage = string.Empty;
    [ObservableProperty] private IBrush _astrometryStatusBrush = ColorNormal;

    // --- REGOLE DI BUSINESS ---
    public AlignmentMode[] AvailableModes => SelectedTarget == AlignmentTarget.Stars 
        ? new[] { AlignmentMode.Automatic } 
        : new[] { AlignmentMode.Automatic, AlignmentMode.Guided, AlignmentMode.Manual };

    public bool IsSearchRadiusVisible => SelectedTarget == AlignmentTarget.Comet && SelectedMode != AlignmentMode.Automatic;

    public bool IsTargetPlacementAllowed => 
        CurrentState == AlignmentState.Initial && 
        SelectedTarget == AlignmentTarget.Comet && 
        SelectedMode != AlignmentMode.Automatic;

    public string CurrentImageText
    {
        get
        {
            if (SelectedTarget == AlignmentTarget.Stars || SelectedMode == AlignmentMode.Automatic) return "1 / 1";
            if (SelectedMode == AlignmentMode.Guided) return $"{(Navigator.CurrentIndex == 0 ? 1 : 2)} / 2";
            return $"{Navigator.DisplayIndex} / {Navigator.TotalCount}";
        }
    }

    public bool IsNavigationVisible => 
        CurrentState != AlignmentState.Calculating && 
        (CurrentState != AlignmentState.Initial || (SelectedTarget == AlignmentTarget.Comet && SelectedMode != AlignmentMode.Automatic));

    public bool IsJplOptionVisible => 
        CurrentState == AlignmentState.Initial &&
        ((SelectedTarget == AlignmentTarget.Comet && SelectedMode != AlignmentMode.Manual) || (SelectedTarget == AlignmentTarget.Stars));

    public Bitmap? SafeImage => ActiveRenderer?.Image;
    public bool IsInteractionEnabled => !IsBusy && !IsProcessingVisible;
    public int MinSearchRadius => 5;
    public int MaxSearchRadius => 500;
    
    public bool IsCalculateButtonVisible => CurrentState == AlignmentState.Initial;
    public bool IsApplyCancelButtonsVisible => CurrentState == AlignmentState.ResultsReady;
    public bool IsProcessingVisible => CurrentState == AlignmentState.Processing || CurrentState == AlignmentState.Calculating;
    public bool IsTargetNameInputVisible => SelectedTarget == AlignmentTarget.Comet;
    public bool IsVerifyButtonVisible => SelectedTarget == AlignmentTarget.Comet;
    public string AstrometryOptionLabel => SelectedTarget == AlignmentTarget.Stars ? "Usa WCS Header" : "Usa NASA/JPL";

    public string ProcessingStatusText => CurrentState switch
    {
        AlignmentState.Calculating => "Analisi centroidi...",
        AlignmentState.Processing => "Salvataggio immagini...",
        _ => "In corso..."
    };

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
        Navigator.IndexFilter = IsIndexAccessible; // [SOLUZIONE PUNTO 3: SALTI]
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

    partial void OnSelectedTargetChanged(AlignmentTarget value) => ResetAlignmentState();
    partial void OnSelectedModeChanged(AlignmentMode value) => ResetAlignmentState();

    private bool IsIndexAccessible(int index)
    {
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

        // [SOLUZIONE PUNTO 1 E 3]: Reset navigazione e aggiornamento frecce
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

        // Gestione cancellazione per navigazione rapida (evita flickering se l'utente preme "Avanti" velocemente)
        _navigationCts?.Cancel();
        _navigationCts?.Dispose();
        _navigationCts = new CancellationTokenSource();
        var token = _navigationCts.Token;

        try
        {
            var file = _files[index];
            var data = await _dataManager.GetDataAsync(file.FilePath);
            token.ThrowIfCancellationRequested();

            // 1. Creazione del renderer tramite factory
            var newRenderer = await _rendererFactory.CreateAsync(data.PixelData, file.ModifiedHeader ?? data.Header);
            token.ThrowIfCancellationRequested();

            // 2. Adattamento contrasto (Logica Scientifica)
            if (ActiveRenderer != null)
            {
                using var nextMat = newRenderer.CaptureScientificMat();
                var profile = ActiveRenderer.GetAdaptedProfileFor(nextMat);
                newRenderer.ApplyContrastProfile(profile);
                
                // Liberiamo subito la memoria della vecchia immagine
                ActiveRenderer.Dispose();
            }

            // 3. Swap del renderer e sincronizzazione Viewport
            ActiveRenderer = newRenderer;
            Viewport.ImageSize = ActiveRenderer.ImageSize;
            Viewport.SearchRadius = SearchRadius; 
            
            // Recuperiamo il mirino specifico per questa immagine dalla lista CoordinateEntries
            Viewport.TargetCoordinate = CoordinateEntries[index].Coordinate;

            // --- SOLUZIONE PUNTO 4: RESET ZOOM INTELLIGENTE ---
            // Se la View ha già comunicato le dimensioni della finestra (ViewportSize > 0),
            // eseguiamo il reset con il margine del 10% definito nel Viewport.
            // Se è 0, non facciamo nulla: ci penserà la View nel metodo CenterImageAsync al termine del caricamento.
            if (Viewport.ViewportSize.Width > 0)
            {
                Viewport.ResetView();
            }
            
            // 4. Notifica cambiamenti per i binding UI
            OnPropertyChanged(nameof(BlackPoint));
            OnPropertyChanged(nameof(WhitePoint));
            OnPropertyChanged(nameof(SafeImage));

            // 5. Aggiornamento stato attivo nella lista laterale
            for (int i = 0; i < CoordinateEntries.Count; i++)
            {
                CoordinateEntries[i].IsActive = (i == index);
            }
        }
        catch (OperationCanceledException) { /* Ignorato: l'utente sta scorrendo velocemente */ }
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
        CurrentState = AlignmentState.Calculating;
        IsBusy = true;
        try
        {
            List<Point2D?> guesses;
            if (UseJplAstrometry)
                guesses = await _coordinator.DiscoverStartingPointsAsync(_files, SelectedTarget, TargetName);
            else
                guesses = CoordinateEntries.Select(e => e.Coordinate.HasValue ? new Point2D(e.Coordinate.Value.X, e.Coordinate.Value.Y) : (Point2D?)null).ToList();

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
            StatusMessage = "Analisi completata.";
        }
        catch (Exception ex) { StatusMessage = ex.Message; CurrentState = AlignmentState.Initial; }
        finally { IsBusy = false; }
    }

    private bool CanCalculate()
    {
        if (IsBusy || IsVerifyingJpl) return false;
        
        // [SOLUZIONE PUNTO 2]: Logica di validazione mirini
        if (SelectedTarget == AlignmentTarget.Stars || SelectedMode == AlignmentMode.Automatic) return true;
        if (SelectedMode == AlignmentMode.Guided) 
            return CoordinateEntries[0].Coordinate != null && CoordinateEntries[_files.Count - 1].Coordinate != null;
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

    [RelayCommand] private void Cancel() => RequestClose?.Invoke();
    private bool CanApply() => CurrentState == AlignmentState.ResultsReady && !IsBusy;

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
        if (CurrentState != AlignmentState.Initial) { ResetAlignmentState(); return; }
        if (!IsTargetPlacementAllowed) return;
        CoordinateEntries[Navigator.CurrentIndex].Coordinate = null;
        Viewport.TargetCoordinate = null;
        CalculateCentersCommand.NotifyCanExecuteChanged();
    }

    partial void OnSearchRadiusChanged(int value) => Viewport.SearchRadius = value;
    
    partial void OnUseJplAstrometryChanged(bool value)
    {
        if (value) _ = VerifyJpl();
        else { AstrometryStatusMessage = ""; AstrometryStatusBrush = ColorNormal; }
    }

    partial void OnTargetNameChanged(string value) => VerifyJplCommand.NotifyCanExecuteChanged();

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