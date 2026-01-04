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
    #region Campi
    
    private readonly IFitsService _fitsService;
    private readonly IAlignmentService _alignmentService;
    private readonly IFitsDataConverter _converter;
    private readonly IImageAnalysisService _analysis;
    
    private readonly List<string> _sourcePaths; 
    private int _currentStackIndex;
    private readonly int _totalStackCount;
    
    private Size _viewportSize;
    private ContrastProfile? _lastContrastProfile;

    // --- COLORI DEFINITI ---
    private static readonly IBrush ColorNormal = Brushes.Cyan;
    private static readonly IBrush ColorSuccess = new SolidColorBrush(Color.Parse("#03A077")); // Verde
    private static readonly IBrush ColorError = new SolidColorBrush(Color.Parse("#E6606A"));   // Rosso
    private static readonly IBrush ColorLoading = new SolidColorBrush(Color.Parse("#8058E8")); // Viola

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
        SelectedTarget == AlignmentTarget.Comet && 
        (SelectedMode == AlignmentMode.Automatic || SelectedMode == AlignmentMode.Guided);

    // --- LOGICA ATTIVAZIONE CHECKBOX ---
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
                    if (SelectedMode == AlignmentMode.Guided)
                    {
                        _ = PrefillGuidedModeAsync();
                    }
                }
                else
                {
                    AstrometryStatusMessage = "";
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

    // ... [Stato e altre proprietà] ...
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
        AlignmentState.Calculating => UseJplAstrometry ? "Scaricamento dati NASA e calcolo centri..." : "Calcolo centri in corso...",
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
        IImageAnalysisService analysis)    
    {
        _fitsService = fitsService;
        _alignmentService = alignmentService;
        _converter = converter;
        _analysis = analysis;
        
        _sourcePaths = sourcePaths; 
        _totalStackCount = sourcePaths.Count;
        _currentStackIndex = 0;
        IsStack = _totalStackCount > 1;

        SelectedTarget = AlignmentTarget.Comet;
        SelectedMode = AlignmentMode.Automatic;
        UseJplAstrometry = false; 
        
        Viewport.SearchRadius = this.SearchRadius;
        
        // Questo comando è SOLO manuale (pulsante Verifica)
        VerifyJplCommand = new RelayCommand(async () => await VerifyJplConnectionAsync(true), () => !IsVerifyingJpl && ActiveImage != null);

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
        catch (Exception ex) { Debug.WriteLine($"--- CRASH IN AlgnVM.InitializeAsync --- {ex}"); }
        finally
        {
            ImageLoadedTcs.TrySetResult();
            CalculateCentersCommand.NotifyCanExecuteChanged();
            ApplyAlignmentCommand.NotifyCanExecuteChanged();
            RefreshNavigationCommands();
        }
    }

    // --- CARICAMENTO IMMAGINE (PULITO) ---
    private async Task LoadStackImageAtIndexAsync(int index)
    {
        if (index < 0 || index >= _totalStackCount) return;
        if (index == _currentStackIndex && ActiveImage != null) return;

        _currentStackIndex = index;
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

        if (_lastContrastProfile != null) newRenderer.ApplyContrastProfile(_lastContrastProfile);
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
        
        // Auto-popolazione nome (se vuoto)
        if (ActiveImage?.Data?.FitsHeader != null && string.IsNullOrWhiteSpace(TargetName))
        {
            string headerObj = ActiveImage.Data.FitsHeader.GetStringValue("OBJECT");
            if (!string.IsNullOrWhiteSpace(headerObj)) TargetName = headerObj.Replace("'", "").Trim();
        }

        // --- SOLO AGGIORNAMENTO VISIVO (Niente chiamate JPL) ---
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

    // ... [Zoom, Pan, ResetView, ResetThresholds, Navigazione invariati] ...
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
    
    // --- Allineamento (Logica Principale) ---
    
    [RelayCommand(CanExecute = nameof(CanCalculateCenters))]
    private async Task CalculateCenters()
    {
        CurrentState = AlignmentState.Calculating; 
        OnPropertyChanged(nameof(ProcessingStatusText)); 

        try
        {
            List<Point?> startingPoints;

            // ---------------------------------------------------------
            // CASO 1: AUTOMATICA + NASA
            // Scarichiamo tutto fresco da JPL per ogni frame.
            // ---------------------------------------------------------
            if (UseJplAstrometry && IsJplOptionVisible && SelectedMode == AlignmentMode.Automatic)
            {
                startingPoints = await PreCalculateJplCentersAsync();
            }
            // ---------------------------------------------------------
            // CASO 2: GUIDATA + NASA (Logica "Path Blending")
            // Usiamo la curva NASA ma ancorata ai TUOI punti manuali.
            // ---------------------------------------------------------
            else if (UseJplAstrometry && IsJplOptionVisible && SelectedMode == AlignmentMode.Guided)
            {
                // 1. Scarica la traiettoria NASA completa (la "forma" del movimento)
                var nasaTrajectory = await PreCalculateJplCentersAsync();
                
                // 2. Recupera le ancore manuali attuali (Start e End)
                var userStart = CoordinateEntries.First().Coordinate;
                var userEnd = CoordinateEntries.Last().Coordinate;

                // Verifica di avere tutti i dati necessari per il calcolo intelligente
                if (nasaTrajectory.Count == _totalStackCount && 
                    userStart.HasValue && userEnd.HasValue &&
                    nasaTrajectory[0].HasValue && nasaTrajectory[^1].HasValue)
                {
                    startingPoints = new List<Point?>();
                    
                    // Calcola il vettore di correzione (Offset) iniziale e finale
                    // "Di quanto l'utente ha corretto la NASA?"
                    Point startOffset = userStart.Value - nasaTrajectory[0].Value;
                    Point endOffset = userEnd.Value - nasaTrajectory[^1].Value;

                    for (int i = 0; i < _totalStackCount; i++)
                    {
                        // Forziamo Start e End ad essere ESATTAMENTE quelli utente
                        if (i == 0)
                        {
                            startingPoints.Add(userStart);
                        }
                        else if (i == _totalStackCount - 1)
                        {
                            startingPoints.Add(userEnd);
                        }
                        else
                        {
                            // Per i frame intermedi:
                            if (nasaTrajectory[i].HasValue)
                            {
                                // Calcola progresso (da 0.0 a 1.0)
                                double t = (double)i / (_totalStackCount - 1);
                                
                                // Interpola l'errore linearmente tra inizio e fine
                                double currentOffsetX = startOffset.X + (endOffset.X - startOffset.X) * t;
                                double currentOffsetY = startOffset.Y + (endOffset.Y - startOffset.Y) * t;

                                // PUNTO CALCOLATO = Punto NASA (forma) + Offset Interpolato (ancoraggio)
                                Point smartPoint = new Point(
                                    nasaTrajectory[i]!.Value.X + currentOffsetX,
                                    nasaTrajectory[i]!.Value.Y + currentOffsetY
                                );
                                
                                startingPoints.Add(smartPoint);
                            }
                            else
                            {
                                startingPoints.Add(null); // Buco nei dati NASA
                            }
                        }
                    }
                }
                else
                {
                    // Fallback: se la NASA fallisce o mancano punti, usa l'interpolazione lineare standard
                    startingPoints = CoordinateEntries.Select(e => e.Coordinate).ToList();
                }
            }
            // ---------------------------------------------------------
            // CASO 3: STANDARD (Manuale o Automatica senza NASA)
            // Usa semplicemente i punti presenti nella lista.
            // ---------------------------------------------------------
            else
            {
                startingPoints = CoordinateEntries.Select(e => e.Coordinate).ToList();
            }

            // Setup per aggiornamento live della UI durante il calcolo
            var progressHandler = new Progress<(int Index, Point? Center)>(update =>
            {
                if (update.Index >= 0 && update.Index < CoordinateEntries.Count)
                    CoordinateEntries[update.Index].Coordinate = update.Center;
            });

            // Chiamata al servizio di allineamento
            var newCoords = await _alignmentService.CalculateCentersAsync(
                SelectedTarget,
                SelectedMode, 
                CenteringMethod.LocalRegion,
                _sourcePaths, 
                startingPoints, // Passiamo la lista "Smart" calcolata sopra
                SearchRadius,
                progressHandler 
            );

            // Aggiornamento finale della lista
            int idx = 0;
            foreach (var coord in newCoords)
            {
                if (idx < CoordinateEntries.Count) CoordinateEntries[idx].Coordinate = coord;
                idx++;
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

    private async Task<List<Point?>> PreCalculateJplCentersAsync()
    {
        var results = new List<Point?>();
        var jplService = new JplHorizonsService();
        
        for (int i = 0; i < _sourcePaths.Count; i++)
        {
            try 
            {
                var header = await _fitsService.ReadHeaderOnlyAsync(_sourcePaths[i]);
                if (header == null) { results.Add(null); continue; }

                int height = header.GetIntValue("NAXIS2"); // Per Flip Y
                var wcsData = WcsHeaderParser.Parse(header);
                string dateObsStr = header.GetStringValue("DATE-OBS");
                DateTime.TryParse(dateObsStr, out DateTime obsDate);
                var location = FitsMetadataReader.ReadObservatoryLocation(header);

                if (!wcsData.IsValid) { results.Add(null); continue; }

                var ephem = await jplService.GetEphemerisAsync(TargetName, obsDate, location);
                
                if (ephem.HasValue)
                {
                    var transform = new WcsTransformation(wcsData);
                    var pixel = transform.WorldToPixel(ephem.Value.Ra, ephem.Value.Dec);
                    
                    if (pixel.HasValue)
                    {
                        // FLIP Y
                        double visualY = height - pixel.Value.Y;
                        results.Add(new Point(pixel.Value.X, visualY));
                    }
                    else results.Add(null);
                }
                else results.Add(null);
            }
            catch { results.Add(null); }
        }
        return results;
    }
    
    private bool CanCalculateCenters()
    {
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
            FinalProcessedPaths = await _alignmentService.ApplyCenteringAndSaveAsync(_sourcePaths, centers, tempFolder);
            DialogResult = true; RequestClose?.Invoke();
        }
        catch (Exception ex) { Debug.WriteLine($"Apply fallito: {ex.Message}"); DialogResult = false; CurrentState = AlignmentState.Initial; }
    }
    
    [RelayCommand] 
    private void CancelCalculation() 
    {
        // 1. Resetta tutto (pulisce coordinate e torna allo stato iniziale)
        ResetAlignmentState();

        // 2. LOGICA AGGIUNTA: Se siamo in Guidata + NASA, ricalcola subito i suggerimenti
        if (SelectedMode == AlignmentMode.Guided && UseJplAstrometry)
        {
            _ = PrefillGuidedModeAsync();
        }
    }
    
    private bool CanApplyAlignment() => CurrentState == AlignmentState.ResultsReady;

    // --- NUOVA FUNZIONE: PRE-FILL GUIDATA (Start & End) ---
    private async Task PrefillGuidedModeAsync()
    {
        if (string.IsNullOrWhiteSpace(TargetName)) return;

        IsVerifyingJpl = true;
        // TESTO RICHIESTO
        AstrometryStatusMessage = "Calcolo coordinate ...";
        AstrometryStatusBrush = ColorLoading;

        // Lista indici da calcolare (0 e Ultimo)
        var indicesToCalc = new List<int> { 0 };
        if (_totalStackCount > 1) indicesToCalc.Add(_totalStackCount - 1);

        try
        {
            foreach (int i in indicesToCalc)
            {
                var result = await FetchJplCoordinateForImage(i);
                if (result.HasValue)
                {
                    CoordinateEntries[i].Coordinate = result.Value;
                    // Se l'utente sta guardando questa immagine, aggiorna il mirino
                    if (i == _currentStackIndex) TargetCoordinate = result.Value;
                }
            }
            // TESTO RICHIESTO
            AstrometryStatusMessage = "Posizioni acquisite da NASA JPL.";
            AstrometryStatusBrush = ColorSuccess;
            CalculateCentersCommand.NotifyCanExecuteChanged();
        }
        catch
        {
            AstrometryStatusMessage = "Errore nel recupero coordinate.";
            AstrometryStatusBrush = ColorError;
        }
        finally
        {
            IsVerifyingJpl = false;
        }
    }

    // --- FUNZIONE VERIFICA MANUALE (Solo immagine corrente) ---
    private async Task VerifyJplConnectionAsync(bool updateReticle = true)
    {
        if (ActiveImage?.Data?.FitsHeader == null || string.IsNullOrWhiteSpace(TargetName))
        {
            AstrometryStatusMessage = "Attenzione: Immagine o nome mancante.";
            AstrometryStatusBrush = ColorNormal;
            return;
        }

        IsVerifyingJpl = true;
        AstrometryStatusMessage = "Connessione a NASA JPL in corso...";
        AstrometryStatusBrush = ColorLoading;

        try
        {
            var result = await FetchJplCoordinateForImage(_currentStackIndex);
            
            if (result.HasValue)
            {
                if (updateReticle)
                {
                    TargetCoordinate = result.Value;
                    CoordinateEntries[_currentStackIndex].Coordinate = result.Value;
                    ApplyAlignmentCommand.NotifyCanExecuteChanged();
                    CalculateCentersCommand.NotifyCanExecuteChanged();
                }
                
                AstrometryStatusMessage = $"Oggetto agganciato a ({result.Value.X:F2}, {result.Value.Y:F2})";
                AstrometryStatusBrush = ColorSuccess;
            }
            else
            {
                AstrometryStatusMessage = "Errore: Oggetto non trovato o errore WCS.";
                AstrometryStatusBrush = ColorError;
            }
        }
        catch (Exception ex)
        {
            AstrometryStatusMessage = $"Errore: {ex.Message}";
            AstrometryStatusBrush = ColorError;
        }
        finally
        {
            IsVerifyingJpl = false;
        }
    }

    // --- HELPER COMUNE PER IL CALCOLO JPL SINGOLO ---
    private async Task<Point?> FetchJplCoordinateForImage(int index)
    {
        string path = _sourcePaths[index];
        var header = await _fitsService.ReadHeaderOnlyAsync(path);
        if (header == null) return null;

        // IMPORTANTE: Leggi altezza per FLIP Y
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
            
            if (pixel.HasValue)
            {
                // *** CORREZIONE FLIP Y ***
                double visualY = height - pixel.Value.Y;
                return new Point(pixel.Value.X, visualY);
            }
        }
        return null;
    }

    #endregion
    
    // ... [Metodi Pubblici e Helpers finali invariati] ...
    #region Metodi Pubblici (Invariati)
    public void ApplyZoomAtPoint(double scaleFactor, Point viewportZoomPoint) { Viewport.ApplyZoomAtPoint(scaleFactor, viewportZoomPoint); OnPropertyChanged(nameof(ZoomStatusText)); }
    public void ApplyPan(double deltaX, double deltaY) => Viewport.ApplyPan(deltaX, deltaY);
    public void SetTargetCoordinate(Point imageCoordinate) 
    { 
        if (CurrentState == AlignmentState.Processing) return;
        if (CurrentState == AlignmentState.Initial && IsStack && SelectedMode == AlignmentMode.Guided) 
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
        if (SelectedMode == AlignmentMode.Automatic) return;
        if (IsStack && SelectedMode == AlignmentMode.Guided) { if (_currentStackIndex != 0 && _currentStackIndex != (_totalStackCount - 1)) return; }
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
        OnPropertyChanged(nameof(AvailableAlignmentModes));
        OnPropertyChanged(nameof(SelectedMode)); 
        OnPropertyChanged(nameof(IsJplOptionVisible));
    }
    
    // --- LOGICA CAMBIO MODALITÀ ---
    partial void OnSelectedModeChanged(AlignmentMode value)
    {
        ResetAlignmentState();
        RefreshNavigationCommands();
        if (!IsIndexAccessible(_currentStackIndex)) _ = NavigateToImage(0); 
        UpdateStackCounterText();
        OnPropertyChanged(nameof(IsJplOptionVisible)); 

        // SE PASSI A GUIDATA E HAI GIÀ ATTIVATO NASA -> CALCOLA SUBITO START/END
        if (value == AlignmentMode.Guided && UseJplAstrometry)
        {
            _ = PrefillGuidedModeAsync();
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