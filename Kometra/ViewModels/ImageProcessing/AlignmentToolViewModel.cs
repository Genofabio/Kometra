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
using Kometra.Models.Fits;
using Kometra.Models.Primitives;
using Kometra.Models.Processing;
using Kometra.Models.Processing.Alignment;
using Kometra.Services.Factories;
using Kometra.Services.Fits;
using Kometra.Services.Processing.Coordinators;
using Kometra.ViewModels.Visualization;
using CoordinateEntry = Kometra.ViewModels.Shared.CoordinateEntry;
using SequenceNavigator = Kometra.ViewModels.Shared.SequenceNavigator;
using Shared_CoordinateEntry = Kometra.ViewModels.Shared.CoordinateEntry;

namespace Kometra.ViewModels.ImageProcessing;

public partial class AlignmentToolViewModel : ObservableObject, IDisposable
{
    private readonly IAlignmentCoordinator _coordinator;
    private readonly IFitsDataManager _dataManager;
    private readonly IFitsRendererFactory _rendererFactory;

    // --- CACHE DATI JPL/WCS ---
    // Memorizza i punti calcolati da JPL/WCS per evitare chiamate ripetute
    // Viene resettata ogni volta che cambia la modalità o il target.
    private List<Point2D?>? _cachedVerifiedPoints;

    public SequenceNavigator Navigator { get; } = new();
    public AlignmentImageViewport Viewport { get; } = new();
    public ObservableCollection<Shared_CoordinateEntry> CoordinateEntries { get; } = new();

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
    [NotifyPropertyChangedFor(nameof(ResultsSectionDescription), nameof(CoordinateListHeader))]
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
    
    [ObservableProperty]
    private bool _cropToCommonArea = true;

    // --- REGOLE DI BUSINESS ---
    public int MinSearchRadius => 0;
    public int MaxSearchRadius => 500;
    
    public bool IsMultipleImages => _files.Count > 1;

    public AlignmentMode[] AvailableModes
    {
        get
        {
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

    // --- TESTI UI DINAMICI ---

    public string CurrentImageText
    {
        get
        {
            if (CurrentState == AlignmentState.ResultsReady)
            {
                return $"{Navigator.DisplayIndex} / {Navigator.TotalCount}";
            }

            int visibleCount = 0;
            int relativeIndex = 0;

            for (int i = 0; i < _files.Count; i++)
            {
                if (IsIndexAccessible(i))
                {
                    visibleCount++;
                    if (i == Navigator.CurrentIndex)
                    {
                        relativeIndex = visibleCount;
                    }
                }
            }

            if (visibleCount == 0) return "0 / 0";
            return $"{relativeIndex} / {visibleCount}";
        }
    }

    public string ResultsSectionTitle => "Rifinitura Manuale";

    public string ResultsSectionDescription => SelectedTarget == AlignmentTarget.Stars
        ? "Il punto indica il centro calcolato in base al campo stellare. Spostalo per correggere l'allineamento dell'intera immagine."
        : "Il punto indica il nucleo della cometa rilevato. Spostalo per correggere la centratura.";

    public string CoordinateListHeader => SelectedTarget == AlignmentTarget.Stars
        ? "Centri Allineamento (Shift)"
        : "Nuclei Rilevati";

    public bool IsNavigationVisible => 
        _files.Count > 1 && 
        CurrentState != AlignmentState.Calculating && 
        (CurrentState == AlignmentState.ResultsReady || (CurrentState == AlignmentState.Initial && SelectedTarget == AlignmentTarget.Comet && SelectedMode != AlignmentMode.Automatic));

    public Bitmap? SafeImage => ActiveRenderer?.Image;
    public bool IsInteractionEnabled => !IsBusy && !IsProcessingVisible;
    public bool IsCalculateButtonVisible => CurrentState == AlignmentState.Initial;
    public bool IsApplyCancelButtonsVisible => CurrentState == AlignmentState.ResultsReady;
    public bool IsProcessingVisible => CurrentState == AlignmentState.Processing || CurrentState == AlignmentState.Calculating;
    
    public bool IsTargetNameInputVisible => SelectedTarget == AlignmentTarget.Comet;
    public bool IsVerifyButtonVisible => SelectedTarget == AlignmentTarget.Comet;
    
    public string AstrometryOptionLabel => SelectedTarget == AlignmentTarget.Stars ? "Usa WCS Header" : "Usa NASA/JPL e WCS Header";
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
        List<FitsFileReference> sourceFiles, 
        IAlignmentCoordinator coordinator,
        IFitsDataManager dataManager,
        IFitsRendererFactory rendererFactory)
    {
        _coordinator = coordinator;
        _dataManager = dataManager;
        _rendererFactory = rendererFactory;
        
        _files = sourceFiles; 
        
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
                CoordinateEntries.Add(new Shared_CoordinateEntry 
                { 
                    Index = i, 
                    DisplayName = System.IO.Path.GetFileName(_files[i].FilePath) 
                });
            }

            var metadata = await _coordinator.GetFileMetadataAsync(_files[0]);
            TargetName = metadata.ObjectName;
            
            SelectedTarget = AlignmentTarget.Comet;
            SelectedMode = AlignmentMode.Automatic;

            await LoadImageAsync(0);
            ImageLoadedTcs.TrySetResult(true);
        }
        catch (Exception ex)
        { 
            StatusMessage = $"Errore inizializzazione: {ex.Message}";
            ImageLoadedTcs.TrySetException(ex);
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
        
        // --- LOGICA DI RESET CACHE ---
        // Se cambiamo modalità, i dati precedenti (es. Automatic) potrebbero non essere
        // più rilevanti o potrebbero dover essere ricalcolati/verificati diversamente.
        // Resettando a null, forziamo il ViewModel a ricalcolarli (o l'utente a riverificarli).
        _cachedVerifiedPoints = null;
        
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
        AstrometryStatusMessage = "Verifica integrità sequenza...";
        
        // Reset preventivo cache in fase di verifica manuale
        _cachedVerifiedPoints = null;

        try
        {
            var pointsResult = await _coordinator.DiscoverStartingPointsAsync(_files, SelectedTarget, TargetName);
            var points = pointsResult.ToList();
        
            int totalFiles = points.Count;
            int validFiles = points.Count(p => p.HasValue);

            if (validFiles == totalFiles)
            {
                // SALVATAGGIO IN CACHE
                _cachedVerifiedPoints = points;

                if (SelectedMode == AlignmentMode.Guided) 
                    ApplyCoordinatesToUi(points);
                else 
                    ClearUiCoordinates(); // Puliamo la UI (i dati sono in cache)

                AstrometryStatusMessage = SelectedTarget == AlignmentTarget.Stars 
                    ? "WCS verificato su tutta la sequenza." 
                    : "Dati JPL/WCS validi per tutti i file.";
                AstrometryStatus = JplStatus.Success;
            }
            else
            {
                int failedCount = totalFiles - validFiles;
                AstrometryStatusMessage = $"Dati mancanti in {failedCount} immagini su {totalFiles}. Risolvi (Plate Solve) prima di procedere.";
                AstrometryStatus = JplStatus.Error;
                ClearUiCoordinates();
            }
        }
        catch (Exception ex)
        {
            AstrometryStatusMessage = $"Errore critico: {ex.Message}";
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
    
    // Invalida la cache se cambia il nome del target
    partial void OnTargetNameChanged(string value) 
    { 
        _cachedVerifiedPoints = null;
        VerifyJplCommand.NotifyCanExecuteChanged(); 
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
            // 1. Recupera input manuali dalla UI
            var inputGuesses = GetCoordinatesFromUi();
            
            // 2. MERGE CON CACHE (Se disponibile)
            // Se abbiamo dati verificati in cache, li usiamo per riempire i buchi (null)
            if (_cachedVerifiedPoints != null && _cachedVerifiedPoints.Count == inputGuesses.Count)
            {
                for (int i = 0; i < inputGuesses.Count; i++)
                {
                    // Priorità all'input manuale, fallback su cache
                    if (inputGuesses[i] == null)
                    {
                        inputGuesses[i] = _cachedVerifiedPoints[i];
                    }
                }
            }

            var progress = new Progress<AlignmentProgressReport>(p => {
                StatusMessage = p.Message;
                if (p.CurrentIndex > 0 && p.FoundCenter.HasValue)
                    UpdateSingleUiCoordinate(p.CurrentIndex - 1, p.FoundCenter.Value);
            });

            int effectiveRadius = SearchRadius;
            
            // 3. DECISIONE FETCH DATI
            // Se _cachedVerifiedPoints non è null, abbiamo già unito i dati sopra -> NON serve fetchare (null).
            // Se _cachedVerifiedPoints è null ma l'utente vuole JPL -> passiamo TargetName -> il Coordinator fetcherà ora.
            string? effectiveJplName = (UseJplAstrometry && SelectedMode != AlignmentMode.Manual && _cachedVerifiedPoints == null) 
                                       ? TargetName 
                                       : null;

            var resultMap = await _coordinator.AnalyzeSequenceAsync(
                files: _files, 
                guesses: inputGuesses, 
                target: SelectedTarget, 
                mode: SelectedMode, 
                method: CenteringMethod.LocalRegion, 
                searchRadius: effectiveRadius, 
                jplTargetName: effectiveJplName, 
                progress: progress);

            ApplyCoordinatesToUi(resultMap.Centers);
            
            CurrentState = AlignmentState.ResultsReady;
            StatusMessage = "Analisi completata. Verifica i risultati.";
            
            UpdateNavigationStatus(); 
            ApplyAlignmentCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(CurrentImageText)); 
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

            int effectiveRadius = SearchRadius; 
            
            // Anche qui, logica per history: se abbiamo la cache usiamo il nome, altrimenti passiamo null o nome
            // (Qui serve solo per il LOG nell'header FITS, quindi passiamo il nome se la feature è attiva)
            string? effectiveJplName = (UseJplAstrometry && SelectedMode != AlignmentMode.Manual) 
                                       ? TargetName 
                                       : null;

            // Nota: Questo calcola la mappa preliminare. La geometria finale (Crop vs Union) 
            // verrà ricalcolata e applicata dentro ExecuteWarpingAsync usando il parametro cropToCommonArea.
            var map = await _coordinator.AnalyzeSequenceAsync(
                files: _files, 
                guesses: centers, 
                target: SelectedTarget, 
                mode: SelectedMode, 
                method: CenteringMethod.LocalRegion, 
                searchRadius: effectiveRadius,
                jplTargetName: effectiveJplName
            );

            // MODIFICA QUI: Passiamo il valore della CheckBox
            FinalProcessedPaths = await _coordinator.ExecuteWarpingAsync(
                files: _files, 
                map: map, 
                mode: SelectedMode,      
                searchRadius: effectiveRadius,  
                jplName: effectiveJplName,
                cropToCommonArea: CropToCommonArea // <--- NUOVO PARAMETRO
            );

            DialogResult = true;
            RequestClose?.Invoke();
        }
        catch (Exception ex) 
        { 
            StatusMessage = $"Errore durante il salvataggio: {ex.Message}"; 
            CurrentState = AlignmentState.Initial; 
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void Cancel()
    {
        if (CurrentState == AlignmentState.ResultsReady) ResetAlignmentState();
        else RequestClose?.Invoke();
    }

    [RelayCommand]
    private async Task SelectImage(Shared_CoordinateEntry entry)
    {
        if (entry == null || Navigator.CurrentIndex == entry.Index) return;
        Navigator.MoveTo(entry.Index);
        await Task.CompletedTask;
    }

    private bool CanCalculate()
    {
        if (IsBusy || IsVerifyingJpl) return false;
    
        if (UseJplAstrometry && SelectedMode != AlignmentMode.Manual)
        {
            if (AstrometryStatus != JplStatus.Success) return false;
        }

        if (SelectedMode == AlignmentMode.Manual) 
            return CoordinateEntries.All(e => e.Coordinate != null);

        if (SelectedTarget == AlignmentTarget.Comet)
        {
            if (UseJplAstrometry && string.IsNullOrWhiteSpace(TargetName)) return false;
        }

        if (SelectedTarget == AlignmentTarget.Stars || SelectedMode == AlignmentMode.Automatic) 
            return true;

        if (SelectedMode == AlignmentMode.Guided) 
            return CoordinateEntries[0].Coordinate != null && CoordinateEntries[^1].Coordinate != null;
    
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
        _navigationCts?.Dispose();
        _navigationCts = new CancellationTokenSource();
        var token = _navigationCts.Token;

        try
        {
            var file = _files[index];
            var data = await _dataManager.GetDataAsync(file.FilePath);
            token.ThrowIfCancellationRequested();

            var imageHdu = data.FirstImageHdu ?? data.PrimaryHdu;
            if (imageHdu == null)
            {
                StatusMessage = "Nessuna immagine valida trovata.";
                return;
            }

            var newRenderer = await _rendererFactory.CreateAsync(
                imageHdu.PixelData, 
                file.ModifiedHeader ?? imageHdu.Header
            );
            
            token.ThrowIfCancellationRequested();

            if (ActiveRenderer != null)
            {
                var currentStyle = ActiveRenderer.CaptureSigmaProfile();
                newRenderer.VisualizationMode = ActiveRenderer.VisualizationMode;
                newRenderer.ApplyRelativeProfile(currentStyle);
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
            OnPropertyChanged(nameof(BlackPoint));
            OnPropertyChanged(nameof(WhitePoint));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) 
        { 
            System.Diagnostics.Debug.WriteLine($"[AlignmentTool] Load Error: {ex.Message}");
            StatusMessage = "Errore rendering immagine."; 
        }
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