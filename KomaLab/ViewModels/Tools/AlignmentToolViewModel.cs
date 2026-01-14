using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Media;     
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KomaLab.Models.Fits;
using KomaLab.Models.Primitives;
using KomaLab.Models.Processing;
using KomaLab.Models.Visualization;
using KomaLab.Services.Astrometry;
using KomaLab.Services.Fits;
using KomaLab.Services.Processing;
using AlignmentImageViewport = KomaLab.ViewModels.Visualization.AlignmentImageViewport;
using CoordinateEntry = KomaLab.ViewModels.Items.CoordinateEntry;
using FitsRenderer = KomaLab.ViewModels.Visualization.FitsRenderer;
using Point = Avalonia.Point;
using Size = Avalonia.Size; 

namespace KomaLab.ViewModels.Tools; 

// ---------------------------------------------------------------------------
// FILE: AlignmentToolViewModel.cs
// RUOLO: Orchestrator per Allineamento Immagini
// ---------------------------------------------------------------------------

public partial class AlignmentToolViewModel : ObservableObject, IDisposable
{
    #region Campi e Costanti
    
    // --- Dipendenze Enterprise ---
    private readonly IFitsIoService _ioService;
    private readonly IFitsMetadataService _metadataService;
    private readonly IAlignmentService _alignmentService;
    private readonly IFitsImageDataConverter _converter;
    private readonly IImageAnalysisService _analysis;
    private readonly IJplHorizonsService _jplService;
    private readonly IMediaExportService _mediaExport; // <--- NUOVA DIPENDENZA AGGIUNTA
    
    // --- Dati Interni ---
    private readonly List<string> _sourcePaths; 
    private int _currentStackIndex;
    private readonly int _totalStackCount;
    
    private Size _viewportSize;
    private ContrastProfile? _lastContrastProfile;
    
    private bool _isAstrometryValid;

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
    private VisualizationMode _visualizationMode;

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
                if (value) _ = RefreshAstrometryStateAsync();
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
    
    private string _astrometryStatusMessage = "";
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

    public ICommand VerifyJplCommand { get; init; }

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
            ? (SelectedTarget == AlignmentTarget.Stars ? "Analisi coordinate WCS..." : "Scaricamento dati NASA...") 
            : "Calcolo centroidi...",
        AlignmentState.Processing => "Salvataggio immagini allineate...",
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
        IJplHorizonsService jplService,
        IMediaExportService mediaExport, // <--- PARAMETRO AGGIUNTO
        VisualizationMode initialMode = VisualizationMode.Linear)
    {
        _ioService = ioService ?? throw new ArgumentNullException(nameof(ioService));
        _metadataService = metadataService ?? throw new ArgumentNullException(nameof(metadataService));
        _alignmentService = alignmentService ?? throw new ArgumentNullException(nameof(alignmentService));
        _converter = converter ?? throw new ArgumentNullException(nameof(converter));
        _analysis = analysis ?? throw new ArgumentNullException(nameof(analysis));
        _jplService = jplService ?? throw new ArgumentNullException(nameof(jplService));
        _mediaExport = mediaExport ?? throw new ArgumentNullException(nameof(mediaExport)); // <--- ASSEGNAZIONE
        
        _sourcePaths = sourcePaths; 
        _totalStackCount = sourcePaths.Count;
        _currentStackIndex = 0;
        IsStack = _totalStackCount > 1;

        _visualizationMode = initialMode;

        SelectedTarget = AlignmentTarget.Comet;
        SelectedMode = AlignmentMode.Automatic;
        UseJplAstrometry = false; 
        
        Viewport.SearchRadius = this.SearchRadius;
        
        // Inizializzazione comando di verifica JPL
        VerifyJplCommand = new RelayCommand(async void () => await RefreshAstrometryStateAsync(), () => !IsVerifyingJpl && ActiveImage != null);

        // Lancio dell'inizializzazione asincrona
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
        catch (Exception ex) 
        { 
            Debug.WriteLine($"AlgnVM.InitializeAsync Error: {ex}"); 
        }
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
        
        // Se stiamo provando a caricare la stessa immagine già attiva, usciamo
        if (index == _currentStackIndex && ActiveImage != null) return;

        // 1. Carichiamo i NUOVI dati PRIMA di scaricare i vecchi.
        // Questo è fondamentale per poter calcolare la differenza statistica tra i due frame.
        FitsImageData? newModel = null;
        try
        {
            newModel = await _ioService.LoadAsync(_sourcePaths[index]);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Errore caricamento immagine {index}: {ex.Message}");
            return;
        }

        if (newModel == null) return;

        // 2. Calcolo Logica Scientifica Soglie (Adaptation)
        ContrastProfile? profileToApply = null;

        if (ActiveImage != null && !ActiveImage.IsDisposed)
        {
            // Usiamo il servizio di analisi per adattare BlackPoint e WhitePoint
            // dalla statistica della vecchia immagine a quella della nuova.
            profileToApply = _analysis.CalculateAdaptedProfile(
                ActiveImage.Data, // Dati Vecchi
                newModel,         // Dati Nuovi
                BlackPoint,       // Soglie UI Correnti
                WhitePoint
            );

            // Solo ORA possiamo scaricare la vecchia immagine per liberare memoria
            // (Nota: _lastContrastProfile serve solo come backup se ActiveImage fosse null)
            _lastContrastProfile = ActiveImage.CaptureContrastProfile();
            ActiveImage.UnloadData();
        }
        else
        {
            // Se non c'è un'immagine attiva (es. primo avvio), usiamo l'ultimo profilo salvato se esiste
            profileToApply = _lastContrastProfile;
        }

        // Aggiornamento indici UI
        _currentStackIndex = index;
        for (int i = 0; i < CoordinateEntries.Count; i++)
        {
            CoordinateEntries[i].IsActive = (i == _currentStackIndex);
        }
        UpdateStackCounterText();

        // 3. Creazione del nuovo Renderer
        // Nota: FitsRenderer calcolerà un AutoStretch di default nel suo InitializeAsync
        var newRenderer = new FitsRenderer(newModel, _converter, _analysis, _mediaExport);
        await newRenderer.InitializeAsync();
        
        newRenderer.VisualizationMode = this.VisualizationMode;

        // 4. Applicazione del Profilo Adattato
        if (profileToApply != null) 
        {
            // Sovrascriviamo l'AutoStretch di default con il nostro profilo adattato
            newRenderer.ApplyContrastProfile(profileToApply);
        }
        else
        {
            // Se è la primissima immagine e non c'è profilo, resettiamo la vista (Zoom/Pan)
            Viewport.ImageSize = newRenderer.ImageSize;
            Viewport.ResetView();
            OnPropertyChanged(nameof(ZoomStatusText));
        }

        // 5. Swap Finale e Binding
        ActiveImage = newRenderer;
        
        // Aggiorniamo le proprietà bindate alla UI (Slider) con i nuovi valori calcolati
        BlackPoint = newRenderer.BlackPoint;
        WhitePoint = newRenderer.WhitePoint;

        OnPropertyChanged(nameof(CorrectImageSize));
        ResetThresholdsCommand.NotifyCanExecuteChanged();
        
        // Logica Header/Target Name (Invariata)
        if (SelectedTarget == AlignmentTarget.Comet && string.IsNullOrWhiteSpace(TargetName) && ActiveImage?.Data.FitsHeader != null)
        {
            string headerObj = ActiveImage.Data.FitsHeader.GetStringValue("OBJECT");
            if (!string.IsNullOrWhiteSpace(headerObj)) 
            {
                TargetName = headerObj.Replace("'", "").Trim();
            }
        }

        UpdateReticleVisibilityForCurrentState();
        UpdateSearchRadiusRange();
        
        ((RelayCommand)VerifyJplCommand).NotifyCanExecuteChanged();
        RefreshNavigationCommands();
    }

    public void Dispose()
    {
        ActiveImage?.UnloadData();
        ActiveImage = null; 
        RequestClose = null; 
        GC.SuppressFinalize(this);
    }
    
    private void RefreshNavigationCommands() 
    { 
        PreviousImageCommand.NotifyCanExecuteChanged(); 
        NextImageCommand.NotifyCanExecuteChanged(); 
        GoToFirstImageCommand.NotifyCanExecuteChanged(); 
        GoToLastImageCommand.NotifyCanExecuteChanged(); 
    }

    #endregion

#region Comandi

    // --- Gestione Viewport ---
    [RelayCommand] private void ZoomIn() { Viewport.ZoomIn(); OnPropertyChanged(nameof(ZoomStatusText)); }
    [RelayCommand] private void ZoomOut() { Viewport.ZoomOut(); OnPropertyChanged(nameof(ZoomStatusText)); }
    [RelayCommand] private void ResetView() { Viewport.ResetView(); OnPropertyChanged(nameof(ZoomStatusText)); }
    
    [RelayCommand(CanExecute = nameof(CanResetThresholds))] 
    private async Task ResetThresholds() 
    { 
        if (ActiveImage == null) return; 
        await ActiveImage.ResetThresholdsAsync(); 
        BlackPoint = ActiveImage.BlackPoint; 
        WhitePoint = ActiveImage.WhitePoint; 
    }
    private bool CanResetThresholds() => ActiveImage != null;

    // --- Navigazione Stack ---
    private async Task NavigateToImage(int newIndex) 
    { 
        if (newIndex < 0 || newIndex >= _totalStackCount || newIndex == _currentStackIndex) return; 
        TargetCoordinate = null; 
        await LoadStackImageAtIndexAsync(newIndex); 
    }

    private int? GetPreviousAccessibleIndex() { for (int i = _currentStackIndex - 1; i >= 0; i--) if (IsIndexAccessible(i)) return i; return null; }
    private int? GetNextAccessibleIndex() { for (int i = _currentStackIndex + 1; i < _totalStackCount; i++) if (IsIndexAccessible(i)) return i; return null; }
    private bool CanShowPrevious() => IsStack && GetPreviousAccessibleIndex().HasValue;
    private bool CanShowNext() => IsStack && GetNextAccessibleIndex().HasValue;

    [RelayCommand(CanExecute = nameof(CanShowPrevious))] private async Task PreviousImage() { var prev = GetPreviousAccessibleIndex(); if (prev.HasValue) await NavigateToImage(prev.Value); }
    [RelayCommand(CanExecute = nameof(CanShowNext))] private async Task NextImage() { var next = GetNextAccessibleIndex(); if (next.HasValue) await NavigateToImage(next.Value); }
    [RelayCommand(CanExecute = nameof(CanShowPrevious))] private async Task GoToFirstImage() => await NavigateToImage(0);
    [RelayCommand(CanExecute = nameof(CanShowNext))] private async Task GoToLastImage() => await NavigateToImage(_totalStackCount - 1);
    
    // --- Allineamento (Logica Core) ---

    [RelayCommand(CanExecute = nameof(CanCalculateCenters))]
    private async Task CalculateCenters()
    {
        CurrentState = AlignmentState.Calculating; 
        OnPropertyChanged(nameof(ProcessingStatusText)); 

        try
        {
            List<Point?> startingPointsUi;

            // CASO A: STELLE + WCS (Priorità Dati Header)
            if (SelectedTarget == AlignmentTarget.Stars && UseJplAstrometry)
            {
                startingPointsUi = await PreCalculateSkyFixedPointsAsync();
            }
            // CASO B: COMETA + JPL + AUTOMATICO (Priorità Effemeridi)
            else if (SelectedTarget == AlignmentTarget.Comet && UseJplAstrometry && SelectedMode == AlignmentMode.Automatic)
            {
                startingPointsUi = await PreCalculateJplCentersAsync();
            }
            // CASO C: COMETA + JPL + GUIDATO (Interpolazione Traiettoria NASA)
            else if (SelectedTarget == AlignmentTarget.Comet && UseJplAstrometry && SelectedMode == AlignmentMode.Guided)
            {
                var nasaTrajectory = await PreCalculateJplCentersAsync();
                var userStart = CoordinateEntries.First().Coordinate;
                var userEnd = CoordinateEntries.Last().Coordinate;

                // Se abbiamo trajectory completa E i click dell'utente su Start/End
                if (nasaTrajectory.Count == _totalStackCount && userStart.HasValue && userEnd.HasValue &&
                    nasaTrajectory[0].HasValue && nasaTrajectory[^1].HasValue)
                {
                    startingPointsUi = new List<Point?>();
                    // Calcolo offset tra click utente e dato NASA per correggere l'errore sistematico
                    Point startOffset = userStart.Value - nasaTrajectory[0]!.Value;
                    Point endOffset = userEnd.Value - nasaTrajectory[^1]!.Value;

                    for (int i = 0; i < _totalStackCount; i++)
                    {
                        if (i == 0) startingPointsUi.Add(userStart);
                        else if (i == _totalStackCount - 1) startingPointsUi.Add(userEnd);
                        else if (nasaTrajectory[i].HasValue)
                        {
                            // Interpolazione lineare dell'offset di correzione
                            double t = (double)i / (_totalStackCount - 1);
                            double ox = startOffset.X + (endOffset.X - startOffset.X) * t;
                            double oy = startOffset.Y + (endOffset.Y - startOffset.Y) * t;
                            
                            Point nasaPt = nasaTrajectory[i]!.Value;
                            startingPointsUi.Add(new Point(nasaPt.X + ox, nasaPt.Y + oy));
                        }
                        else startingPointsUi.Add(null);
                    }
                }
                else 
                {
                    // Fallback: se i dati NASA sono incompleti, usiamo solo i click utente (interpolazione lineare standard)
                    startingPointsUi = CoordinateEntries.Select(e => e.Coordinate).ToList();
                }
            }
            // CASO D: STANDARD (Nessun dato esterno / Allineamento visuale puro)
            else 
            {
                startingPointsUi = CoordinateEntries.Select(e => e.Coordinate).ToList();
                
                // --- FIX LOGICA GUESSES ---
                
                if (SelectedTarget == AlignmentTarget.Comet)
                {
                    // 1. SE AUTOMATICO: Vogliamo "Blind Mode". 
                    // Passiamo esplicitamente NULL per evitare che la Strategy usi il centro come "Suggerimento" e restringa il raggio.
                    if (SelectedMode == AlignmentMode.Automatic)
                    {
                        startingPointsUi = new List<Point?>(new Point?[_totalStackCount]);
                    }
                    // 2. SE MANUALE/GUIDATA: Se l'utente non ha cliccato nulla, offriamo il centro come fallback di comodità.
                    else if (startingPointsUi.All(p => p == null) && ActiveImage != null)
                    {
                        startingPointsUi = Enumerable.Repeat<Point?>(
                            new Point(ActiveImage.ImageSize.Width / 2, ActiveImage.ImageSize.Height / 2), 
                            _totalStackCount
                        ).ToList();
                    }
                }
            }

            // Conversione Domain Model (Point -> Point2D)
            var domainStartingPoints = startingPointsUi
                .Select(p => p.HasValue ? (Point2D?)new Point2D(p.Value.X, p.Value.Y) : null)
                .ToList();
            
            // Handler per aggiornare la UI in tempo reale
            var progressHandler = new Progress<(int Index, Point2D? Center)>(update => {
                if (update.Index >= 0 && update.Index < CoordinateEntries.Count)
                    CoordinateEntries[update.Index].Coordinate = update.Center.HasValue 
                        ? new Point(update.Center.Value.X, update.Center.Value.Y) 
                        : null;
            });

            // Chiamata al Servizio
            var newCoordsDomain = await _alignmentService.CalculateCentersAsync(
                SelectedTarget, 
                SelectedMode, 
                CenteringMethod.LocalRegion, 
                _sourcePaths, 
                domainStartingPoints, 
                SearchRadius, 
                progressHandler 
            );

            // Aggiornamento finale UI (per sicurezza, nel caso il progress saltasse l'ultimo)
            int resultIdx = 0;
            foreach (var coord in newCoordsDomain)
            {
                if (resultIdx < CoordinateEntries.Count)
                    CoordinateEntries[resultIdx].Coordinate = coord.HasValue 
                        ? new Point(coord.Value.X, coord.Value.Y) 
                        : null;
                resultIdx++;
            }

            CurrentState = AlignmentState.ResultsReady;
            UpdateReticleVisibilityForCurrentState();
            ApplyAlignmentCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex)
        {
            AstrometryStatusMessage = $"Errore: {ex.Message}";
            AstrometryStatusBrush = ColorError;
            CurrentState = AlignmentState.Initial;
        }
    }

    private bool CanCalculateCenters()
    {
        if (UseJplAstrometry)
        {
            if (IsVerifyingJpl || !_isAstrometryValid) return false;
        }
        var currentCoordsDomain = CoordinateEntries.Select(e => e.Coordinate.HasValue ? new Point2D(e.Coordinate.Value.X, e.Coordinate.Value.Y) : (Point2D?)null).ToList();
        return _alignmentService.CanCalculate(SelectedTarget, SelectedMode, currentCoordsDomain, _totalStackCount);
    }

    [RelayCommand(CanExecute = nameof(CanApplyAlignment))]
    private async Task ApplyAlignment()
    {
        CurrentState = AlignmentState.Processing;
        try
        {
            var centersDomain = CoordinateEntries.Select(e => e.Coordinate.HasValue ? new Point2D(e.Coordinate.Value.X, e.Coordinate.Value.Y) : (Point2D?)null).ToList();
            string tempFolder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Komalab", "Aligned");
            
            FinalProcessedPaths = await _alignmentService.ApplyCenteringAndSaveAsync(_sourcePaths, centersDomain, tempFolder, SelectedTarget);
            DialogResult = true; 
            RequestClose?.Invoke();
        }
        catch (Exception ex) 
        { 
            Debug.WriteLine($"Apply fallito: {ex.Message}"); 
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
        IsVerifyingJpl = true;
        _isAstrometryValid = false;
        CalculateCentersCommand.NotifyCanExecuteChanged();

        if (SelectedTarget == AlignmentTarget.Comet && SelectedMode == AlignmentMode.Manual)
        {
            AstrometryStatusMessage = ""; IsVerifyingJpl = false; return; 
        }

        if (ActiveImage == null || _sourcePaths.Count == 0)
        {
            AstrometryStatusMessage = "Nessuna immagine caricata.";
            AstrometryStatusBrush = ColorError; IsVerifyingJpl = false; return;
        }

        AstrometryStatusBrush = ColorLoading;
        try
        {
            if (SelectedTarget == AlignmentTarget.Stars)
            {
                AstrometryStatusMessage = "Verifica WCS...";
                var p = await FetchSkyCoordinateForImage(_currentStackIndex);
                if (p.HasValue)
                {
                    if (IsTargetMarkerVisible) TargetCoordinate = null; 
                    AstrometryStatusMessage = "WCS valido."; AstrometryStatusBrush = ColorSuccess; _isAstrometryValid = true;
                }
                else throw new InvalidOperationException("Dati WCS non validi.");
            }
            else if (SelectedTarget == AlignmentTarget.Comet)
            {
                AstrometryStatusMessage = "Analisi stack...";
                for (int i = 0; i < _sourcePaths.Count; i++)
                {
                    var h = await _ioService.ReadHeaderOnlyAsync(_sourcePaths[i]);
                    if (h == null || _metadataService.GetObservationDate(h) == null) throw new InvalidOperationException($"Frame {i+1}: Metadati temporali mancanti.");
                    var w = _metadataService.ExtractWcs(h);
                    if (w == null || !w.IsValid) throw new InvalidOperationException($"Frame {i+1}: Astrometria mancante.");
                }

                AstrometryStatusMessage = "Interrogazione NASA JPL...";
                if (SelectedMode == AlignmentMode.Guided)
                {
                    var pStart = await FetchJplCoordinateForImage(0);
                    var pEnd = await FetchJplCoordinateForImage(_totalStackCount - 1);
                    if (pStart.HasValue && pEnd.HasValue)
                    {
                        CoordinateEntries[0].Coordinate = pStart;
                        CoordinateEntries[_totalStackCount - 1].Coordinate = pEnd;
                        TargetCoordinate = (_currentStackIndex == 0) ? pStart : ((_currentStackIndex == _totalStackCount - 1) ? pEnd : null);
                        AstrometryStatusMessage = "Traiettoria caricata."; AstrometryStatusBrush = ColorSuccess; _isAstrometryValid = true;
                    }
                    else throw new InvalidOperationException("Target fuori campo.");
                }
                else
                {
                    var p = await FetchJplCoordinateForImage(_currentStackIndex);
                    if (p.HasValue) { AstrometryStatusMessage = "Dati JPL pronti."; AstrometryStatusBrush = ColorSuccess; _isAstrometryValid = true; }
                    else throw new InvalidOperationException("Target non trovato.");
                }
            }
        }
        catch (Exception ex) { AstrometryStatusMessage = ex.Message; AstrometryStatusBrush = ColorError; }
        finally { IsVerifyingJpl = false; CalculateCentersCommand.NotifyCanExecuteChanged(); }
    }

    private void UpdateReticle(Point point)
    {
        TargetCoordinate = point;
        CoordinateEntries[_currentStackIndex].Coordinate = point;
        ApplyAlignmentCommand.NotifyCanExecuteChanged();
        CalculateCentersCommand.NotifyCanExecuteChanged();
    }

    // --- HELPERS JPL / WCS ---

    private async Task<List<Point?>> PreCalculateJplCentersAsync()
    {
        var results = new List<Point?>();
        for (int i = 0; i < _sourcePaths.Count; i++) results.Add(await FetchJplCoordinateForImage(i));
        return results;
    }

    private async Task<Point?> FetchJplCoordinateForImage(int index)
    {
        var header = await _ioService.ReadHeaderOnlyAsync(_sourcePaths[index]);
        if (header == null) return null;

        var obsDate = _metadataService.GetObservationDate(header);
        var location = _metadataService.GetObservatoryLocation(header);
        var wcsData = _metadataService.ExtractWcs(header);
        
        var ephem = await _jplService.GetEphemerisAsync(TargetName, obsDate!.Value, location);
        if (ephem.HasValue)
        {
            int height = header.GetIntValue("NAXIS2");
            var transform = new WcsTransformation(wcsData, height);
            var pixel = transform.WorldToPixel(ephem.Value.Ra, ephem.Value.Dec);
            return pixel.HasValue ? new Point(pixel.Value.X, pixel.Value.Y) : null;
        }
        return null;
    }

    private async Task<Point?> FetchSkyCoordinateForImage(int index)
    {
        var refHeader = await _ioService.ReadHeaderOnlyAsync(_sourcePaths[0]);
        if (refHeader != null)
        {
            var refWcs = _metadataService.ExtractWcs(refHeader);
            if (!refWcs.IsValid) return null; 

            var header = await _ioService.ReadHeaderOnlyAsync(_sourcePaths[index]);
            if (header != null)
            {
                var wcs = _metadataService.ExtractWcs(header);
                if (wcs.IsValid) 
                {
                    int height = header.GetIntValue("NAXIS2");
                    var transform = new WcsTransformation(wcs, height);
                    var pixel = transform.WorldToPixel(refWcs.RefRaDeg, refWcs.RefDecDeg);
                    return pixel.HasValue ? new Point(pixel.Value.X, pixel.Value.Y) : null;
                }
            }
        }

        return null;
    }

    private async Task<List<Point?>> PreCalculateSkyFixedPointsAsync()
    {
        var rawPoints = new List<Point?>();
        for (int i = 0; i < _sourcePaths.Count; i++) rawPoints.Add(await FetchSkyCoordinateForImage(i));

        if (rawPoints.Count > 0 && rawPoints[0].HasValue)
        {
            var h0 = await _ioService.ReadHeaderOnlyAsync(_sourcePaths[0]);
            if (h0 != null)
            {
                var offset = new Point(h0.GetIntValue("NAXIS1") / 2.0, h0.GetIntValue("NAXIS2") / 2.0) - rawPoints[0]!.Value;
                return rawPoints.Select(p => p.HasValue ? (Point?)(p.Value + offset) : null).ToList();
            }
        }
        return rawPoints;
    }

    #endregion
    
    #region Metodi Pubblici

    public void ApplyZoomAtPoint(double scaleFactor, Point viewportZoomPoint) 
    { 
        Viewport.ApplyZoomAtPoint(scaleFactor, viewportZoomPoint); 
        OnPropertyChanged(nameof(ZoomStatusText)); 
    }

    public void ApplyPan(double deltaX, double deltaY) => Viewport.ApplyPan(deltaX, deltaY);

    public void SetTargetCoordinate(Point imageCoordinate) 
    { 
        // 1. Se sta calcolando/salvando, ignora tutto.
        if (CurrentState == AlignmentState.Processing || CurrentState == AlignmentState.Calculating) return;

        // 2. LOGICA FASE INIZIALE (SETUP)
        // Qui applichiamo le restrizioni per guidare l'utente nell'input corretto.
        if (CurrentState == AlignmentState.Initial)
        {
            // In Automatico, durante il setup, non si clicca nulla.
            if (SelectedMode == AlignmentMode.Automatic) return;

            // In Guidato (Cometa), durante il setup, si clicca solo Start ed End.
            if (IsStack && SelectedMode == AlignmentMode.Guided && SelectedTarget == AlignmentTarget.Comet) 
            { 
                if (_currentStackIndex != 0 && _currentStackIndex != (_totalStackCount - 1)) return; 
            }
        }

        // --- APPLICAZIONE COORDINATA ---
        
        TargetCoordinate = imageCoordinate; 
        
        if (_currentStackIndex >= 0 && _currentStackIndex < CoordinateEntries.Count) 
        {
            CoordinateEntries[_currentStackIndex].Coordinate = imageCoordinate;
        }

        // Se siamo in fase iniziale, sblocchiamo il tasto "Calcola" se i requisiti sono soddisfatti.
        // Se siamo in fase risultati, sblocchiamo "Applica" (anche se è sempre attivo in teoria).
        ApplyAlignmentCommand.NotifyCanExecuteChanged(); 
        CalculateCentersCommand.NotifyCanExecuteChanged(); 
    }

    public void ClearTarget() 
    { 
        if (CurrentState != AlignmentState.Initial) 
        { 
            ResetAlignmentState(); 
            return; 
        }

        if (SelectedMode == AlignmentMode.Automatic && SelectedTarget == AlignmentTarget.Comet) return;

        if (IsStack && SelectedMode == AlignmentMode.Guided && SelectedTarget == AlignmentTarget.Comet) 
        { 
            if (_currentStackIndex != 0 && _currentStackIndex != (_totalStackCount - 1)) return; 
        }

        TargetCoordinate = null; 
        
        if (_currentStackIndex >= 0 && _currentStackIndex < CoordinateEntries.Count) 
        {
            CoordinateEntries[_currentStackIndex].Coordinate = null;
        }

        ApplyAlignmentCommand.NotifyCanExecuteChanged(); 
        CalculateCentersCommand.NotifyCanExecuteChanged(); 
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

        if (!IsIndexAccessible(_currentStackIndex)) 
        {
            _ = NavigateToImage(0); 
        }

        UpdateStackCounterText();
        OnPropertyChanged(nameof(IsJplOptionVisible)); 
        
        if (UseJplAstrometry) _ = RefreshAstrometryStateAsync();
    }

    partial void OnCurrentStateChanged(AlignmentState value) 
    { 
        RefreshNavigationCommands(); 
        UpdateStackCounterText();
        OnPropertyChanged(nameof(IsJplOptionVisible)); 
        OnPropertyChanged(nameof(IsSearchRadiusControlsVisible));
    }

    private void ResetAlignmentState() 
    { 
        CurrentState = AlignmentState.Initial; 
        foreach (var entry in CoordinateEntries) 
        {
            entry.Coordinate = null; 
        }
        TargetCoordinate = null; 
        CalculateCentersCommand.NotifyCanExecuteChanged(); 
        ApplyAlignmentCommand.NotifyCanExecuteChanged(); 
    }

    private void UpdateReticleVisibilityForCurrentState() 
    { 
        if (CurrentState == AlignmentState.Initial && SelectedMode == AlignmentMode.Automatic)
        {
            TargetCoordinate = null;
            return;
        }

        if (_currentStackIndex < 0 || _currentStackIndex >= CoordinateEntries.Count) 
        { 
            TargetCoordinate = null; 
            return; 
        } 
        
        var currentEntry = CoordinateEntries[_currentStackIndex]; 
        TargetCoordinate = currentEntry.Coordinate; 
    }

    private void UpdateSearchRadiusRange() 
    { 
        if (ActiveImage == null || ActiveImage.ImageSize == default(Size)) 
        { 
            MinSearchRadius = 0; 
            MaxSearchRadius = 100; 
        } 
        else 
        { 
            double minDimension = Math.Min(ActiveImage.ImageSize.Width, ActiveImage.ImageSize.Height); 
            MinSearchRadius = 0; 
            MaxSearchRadius = (int)Math.Floor(minDimension / 2.0); 
        } 
        SearchRadius = Math.Clamp(SearchRadius, MinSearchRadius, MaxSearchRadius); 
    }

    private bool IsIndexAccessible(int index) 
    { 
        if (CurrentState != AlignmentState.Initial) return true;
        
        if (index < 0 || index >= _totalStackCount) return false;
        
        if (SelectedTarget == AlignmentTarget.Stars) return index == 0;
        
        return SelectedMode switch {
            AlignmentMode.Automatic => index == 0,
            AlignmentMode.Guided => index == 0 || index == _totalStackCount - 1,
            _ => true 
        };
    }

    private void UpdateStackCounterText() 
    {
        if (CurrentState != AlignmentState.Initial || SelectedMode == AlignmentMode.Manual) 
        { 
            StackCounterText = $"{_currentStackIndex + 1} / {_totalStackCount}"; 
            return; 
        }
        
        if (SelectedMode == AlignmentMode.Automatic || SelectedTarget == AlignmentTarget.Stars) 
        { 
            StackCounterText = "1 / 1"; 
            return; 
        }
        
        if (SelectedMode == AlignmentMode.Guided) 
        { 
            int visibleStep = (_currentStackIndex == 0) ? 1 : 2; 
            StackCounterText = $"{visibleStep} / 2"; 
        }
    }
    #endregion
}