using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KomaLab.Services;
using System.Diagnostics;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Media.Imaging;
using KomaLab.Models;
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
    
    private bool _hasLockedThresholds;
    private double _lockedKBlack; 
    private double _lockedKWhite;
    private bool _isInternalChange;

    #endregion

    #region Proprietà
    
    public List<string>? FinalProcessedPaths { get; private set; }
    public ViewportManager Viewport { get; } = new();
    public ObservableCollection<CoordinateEntry> CoordinateEntries { get; } = new();

    public Size ViewportSize
    {
        get => _viewportSize;
        set
        {
            _viewportSize = value;
            Viewport.ViewportSize = value; 
        }
    }
    
    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(CorrectImageSize))]
    [NotifyPropertyChangedFor(nameof(SafeImage))]       // Per Binding Safe
    [NotifyPropertyChangedFor(nameof(SafeImageWidth))]  // Per Binding Safe
    [NotifyPropertyChangedFor(nameof(SafeImageHeight))] // Per Binding Safe
    private FitsRenderer? _activeImage; 
    
    public Size CorrectImageSize => ActiveImage?.ImageSize ?? default;
    
    // --- PROPRIETÀ SAFE (Per evitare errori Binding in chiusura) ---
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSearchRadiusVisible))]
    [NotifyPropertyChangedFor(nameof(IsSearchRadiusControlsVisible))]
    [NotifyPropertyChangedFor(nameof(IsRefinementMessageVisible))]
    [NotifyPropertyChangedFor(nameof(IsNavigationVisible))]
    private AlignmentMode _selectedMode = AlignmentMode.Automatic;
    
    public bool IsSearchRadiusVisible => SelectedMode != AlignmentMode.Automatic;

    [ObservableProperty] private int _minSearchRadius;
    [ObservableProperty] private int _maxSearchRadius = 100;
    [ObservableProperty] private int _searchRadius = 100;

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
        AlignmentState.Calculating => "Calcolo centri in corso...",
        AlignmentState.Processing => "Allineamento in corso...",
        _ => "Elaborazione..."
    };

    public bool IsNavigationVisible
    {
        get
        {
            if (!IsStack) return false;

            // 1. Durante il calcolo iniziale (barra 1) nascondiamo tutto
            if (CurrentState == AlignmentState.Calculating) return false;

            // 2. Se i risultati sono pronti OPPURE stiamo applicando (barra 2), mostriamo la navigazione
            if (CurrentState == AlignmentState.ResultsReady || CurrentState == AlignmentState.Processing) 
                return true;

            // 3. Logica stato Initial
            return SelectedMode != AlignmentMode.Automatic;
        }
    }
    
    public bool IsCoordinateListVisible => CurrentState == AlignmentState.ResultsReady || CurrentState == AlignmentState.Processing;
    public IEnumerable<AlignmentMode> AvailableAlignmentModes { get; private set; }
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

        if (IsStack)
        {
            AvailableAlignmentModes = new[] { AlignmentMode.Automatic, AlignmentMode.Guided, AlignmentMode.Manual };
        }
        else
        {
            AvailableAlignmentModes = new[] { AlignmentMode.Automatic, AlignmentMode.Manual };
        }
        
        Viewport.SearchRadius = this.SearchRadius;
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
            
                CoordinateEntries.Add(new CoordinateEntry
                {
                    Index = i,
                    DisplayName = fileName,
                    Coordinate = null,
                    ImageHeight = 0 // Sarà aggiornato al load
                });
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
            Debug.WriteLine($"--- CRASH IN AlgnVM.InitializeAsync --- {ex}");
        }
        finally
        {
            ImageLoadedTcs.TrySetResult();
            CalculateCentersCommand.NotifyCanExecuteChanged();
            ApplyAlignmentCommand.NotifyCanExecuteChanged();
            RefreshNavigationCommands();
        }
    }

    // --- METODO CRITICO PER LA MEMORIA ---
    private async Task LoadStackImageAtIndexAsync(int index)
    {
        // 1. Validazione input
        if (index < 0 || index >= _totalStackCount) return;
        
        // Evitiamo di ricaricare se siamo già sulla stessa immagine e l'immagine è caricata
        if (index == _currentStackIndex && ActiveImage != null) return;

        _currentStackIndex = index;
        UpdateStackCounterText();

        FitsRenderer? newRenderer = null;

        // 2. Caricamento ON-DEMAND (IO asincrono)
        try
        {
            string path = _sourcePaths[index];
            var newModel = await _fitsService.LoadFitsFromFileAsync(path);
            
            if (newModel != null)
            {
                newRenderer = new FitsRenderer(newModel, _fitsService, _converter, _analysis);
                await newRenderer.InitializeAsync();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Errore caricamento immagine stack [{index}]: {ex.Message}");
            newRenderer?.UnloadData();
            return; // O gestire diversamente l'errore
        }

        // Variabili per memorizzare i valori da applicare alla UI alla fine
        double targetBlack = 0;
        double targetWhite = 0;
        bool shouldUpdateThresholds = false;

        // 3. Calcolo Statistiche e Logica Sigma Locking (Senza aggiornare ancora le Property)
        if (newRenderer != null)
        {
            var (mean, sigma) = newRenderer.GetImageStatistics();

            if (!_hasLockedThresholds)
            {
                // PRIMA VOLTA: Calcoliamo i fattori K basati sui valori di default dell'immagine
                double masterBlack = newRenderer.BlackPoint;
                double masterWhite = newRenderer.WhitePoint;
                
                _lockedKBlack = (masterBlack - mean) / sigma;
                _lockedKWhite = (masterWhite - mean) / sigma;
                _hasLockedThresholds = true;
                
                // Salviamo i valori target
                targetBlack = masterBlack;
                targetWhite = masterWhite;
                shouldUpdateThresholds = true;

                // Reset vista solo al primo avvio
                Viewport.ImageSize = newRenderer.ImageSize;
                Viewport.ResetView(); 
                OnPropertyChanged(nameof(ZoomStatusText));
            }
            else
            {
                // VOLTE SUCCESSIVE: Calcoliamo le nuove soglie basandoci sui fattori K bloccati
                // e le statistiche della NUOVA immagine
                double newBlack = mean + (_lockedKBlack * sigma);
                double newWhite = mean + (_lockedKWhite * sigma);
                
                // Impostiamo i valori interni del renderer (non triggera ancora la UI)
                newRenderer.BlackPoint = newBlack;
                newRenderer.WhitePoint = newWhite;
                
                // Salviamo i valori target
                targetBlack = newBlack;
                targetWhite = newWhite;
                shouldUpdateThresholds = true;
            }
        }

        // 4. CRITICO: Swap dei Renderer (Low RAM Strategy)
        // È fondamentale farlo ORA, prima di toccare BlackPoint/WhitePoint properties
        var oldRenderer = ActiveImage;
        
        ActiveImage = newRenderer; // Questo aggiorna anche SafeImage
        
        // Distruggiamo subito il vecchio per liberare RAM
        oldRenderer?.UnloadData(); 

        // 5. Aggiornamenti UI dipendenti dall'immagine
        OnPropertyChanged(nameof(CorrectImageSize)); 
        ResetThresholdsCommand.NotifyCanExecuteChanged();
        UpdateReticleVisibilityForCurrentState();
        UpdateSearchRadiusRange();
        RefreshNavigationCommands();

        // 6. Aggiornamento delle Proprietà Observable (Triggera OnChanged)
        if (shouldUpdateThresholds)
        {
            try 
            {
                _isInternalChange = true; 
                BlackPoint = targetBlack;
                WhitePoint = targetWhite;
            }
            finally
            {
                _isInternalChange = false;
            }
        }
    }

    public void Dispose()
    {
        ActiveImage?.UnloadData();
        ActiveImage = null; // Notifica SafeImage -> View legge null -> Nessun errore
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

    // ... (Zoom, Pan, ResetView invariati) ...
    [RelayCommand] private void ZoomIn() 
    {
        Viewport.ZoomIn();
        OnPropertyChanged(nameof(ZoomStatusText));
    }

    [RelayCommand] private void ZoomOut() 
    {
        Viewport.ZoomOut();
        OnPropertyChanged(nameof(ZoomStatusText));
    }
    
    [RelayCommand] private void ResetView() 
    {
        Viewport.ResetView();
        OnPropertyChanged(nameof(ZoomStatusText));
    }
    
    [RelayCommand(CanExecute = nameof(CanResetThresholds))]
    private async Task ResetThresholds() 
    {
        if (ActiveImage == null) return;
        await ApplyOptimalStretchAsync();
        var (mean, sigma) = ActiveImage.GetImageStatistics();
        _lockedKBlack = (ActiveImage.BlackPoint - mean) / sigma;
        _lockedKWhite = (ActiveImage.WhitePoint - mean) / sigma;
        _hasLockedThresholds = true; 
    }
    private bool CanResetThresholds() => ActiveImage != null;
    
    // --- Navigazione ---
    
    private async Task NavigateToImage(int newIndex)
    {
        if (newIndex < 0 || newIndex >= _totalStackCount || newIndex == _currentStackIndex)
            return;
        TargetCoordinate = null; 
        await LoadStackImageAtIndexAsync(newIndex);
    }

    private int? GetPreviousAccessibleIndex()
    {
        for (int i = _currentStackIndex - 1; i >= 0; i--)
            if (IsIndexAccessible(i)) return i;
        return null;
    }

    private int? GetNextAccessibleIndex()
    {
        for (int i = _currentStackIndex + 1; i < _totalStackCount; i++)
            if (IsIndexAccessible(i)) return i;
        return null;
    }

    private bool CanShowPrevious() => IsStack && GetPreviousAccessibleIndex().HasValue;
    private bool CanShowNext() => IsStack && GetNextAccessibleIndex().HasValue;

    [RelayCommand(CanExecute = nameof(CanShowPrevious))]
    private async Task PreviousImage()
    {
        var prevIndex = GetPreviousAccessibleIndex();
        if (prevIndex.HasValue) await NavigateToImage(prevIndex.Value);
    }

    [RelayCommand(CanExecute = nameof(CanShowNext))]
    private async Task NextImage()
    {
        var nextIndex = GetNextAccessibleIndex();
        if (nextIndex.HasValue) await NavigateToImage(nextIndex.Value);
    }
    
    [RelayCommand(CanExecute = nameof(CanShowPrevious))] 
    private async Task GoToFirstImage() => await NavigateToImage(0);

    [RelayCommand(CanExecute = nameof(CanShowNext))]
    private async Task GoToLastImage() => await NavigateToImage(_totalStackCount - 1);
    
    // --- Allineamento ---

    [RelayCommand(CanExecute = nameof(CanCalculateCenters))]
    private async Task CalculateCenters()
    {
        CurrentState = AlignmentState.Calculating; 

        try
        {
            var currentCoords = CoordinateEntries.Select(e => e.Coordinate);
            
            // 1. CREIAMO IL GESTORE DEL PROGRESSO
            // Questo blocco di codice viene eseguito automaticamente sul thread della UI
            // ogni volta che il service chiama 'progress.Report(...)'
            var progressHandler = new Progress<(int Index, Point? Center)>(update =>
            {
                // Aggiornamento in tempo reale del singolo elemento
                if (update.Index >= 0 && update.Index < CoordinateEntries.Count)
                {
                    CoordinateEntries[update.Index].Coordinate = update.Center;
                }
            });

            // 2. CHIAMATA AL SERVICE
            // Passiamo 'progressHandler' come ultimo argomento
            var newCoords = await _alignmentService.CalculateCentersAsync(
                SelectedMode, 
                CenteringMethod.LocalRegion,
                _sourcePaths, 
                currentCoords, 
                SearchRadius,
                progressHandler // <--- PASSAGGIO FONDAMENTALE
            );

            // 3. SINCRONIZZAZIONE FINALE (per sicurezza)
            // Sovrascriviamo tutto alla fine per essere certi al 100% che la lista sia coerente
            int i = 0;
            foreach (var coord in newCoords)
            {
                if (i < CoordinateEntries.Count) CoordinateEntries[i].Coordinate = coord;
                i++;
            }

            CurrentState = AlignmentState.ResultsReady;
            
            UpdateReticleVisibilityForCurrentState();
            ApplyAlignmentCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Errore calcolo: {ex}");
            CurrentState = AlignmentState.Initial;
        }
    }
    
    private bool CanCalculateCenters()
    {
        var currentCoords = CoordinateEntries.Select(e => e.Coordinate).ToList();
        return _alignmentService.CanCalculate(SelectedMode, currentCoords, _totalStackCount);
    }

    [RelayCommand(CanExecute = nameof(CanApplyAlignment))]
    private async Task ApplyAlignment()
    {
        CurrentState = AlignmentState.Processing;
        try
        {
            var centers = CoordinateEntries.Select(e => e.Coordinate).ToList();
            string tempFolder = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), 
                "Komalab", 
                "Aligned"
            );
            Debug.WriteLine($"[TEMP PATH CHECK] Sto salvando in: {tempFolder}");

            // Passiamo i PATHS
            FinalProcessedPaths = await _alignmentService.ApplyCenteringAndSaveAsync(
                _sourcePaths, 
                centers, 
                tempFolder);
        
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
    
    [RelayCommand] private void CancelCalculation() => ResetAlignmentState();
    
    private bool CanApplyAlignment() => CurrentState == AlignmentState.ResultsReady;

    #endregion
    
    #region Metodi Pubblici

    // ... (Public methods for Code-Behind) ...
    public void ApplyZoomAtPoint(double scaleFactor, Point viewportZoomPoint)
    {
        Viewport.ApplyZoomAtPoint(scaleFactor, viewportZoomPoint);
        OnPropertyChanged(nameof(ZoomStatusText));
    }

    public void ApplyPan(double deltaX, double deltaY) => Viewport.ApplyPan(deltaX, deltaY);

    public void SetTargetCoordinate(Point imageCoordinate)
    {
        if (CurrentState == AlignmentState.Processing) return;

        if (CurrentState == AlignmentState.Initial) 
        {
            if (SelectedMode == AlignmentMode.Automatic) return; 
            if (IsStack && SelectedMode == AlignmentMode.Guided)
            {
                if (_currentStackIndex != 0 && _currentStackIndex != (_totalStackCount - 1)) return;
            }
        }
    
        TargetCoordinate = imageCoordinate; 
        if (_currentStackIndex >= 0 && _currentStackIndex < CoordinateEntries.Count)
        {
            CoordinateEntries[_currentStackIndex].Coordinate = imageCoordinate;
        }
    
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
    
        if (SelectedMode == AlignmentMode.Automatic) return;
        if (IsStack && SelectedMode == AlignmentMode.Guided)
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

    // ... (Partial methods e helpers Sigma Locking) ...
    partial void OnBlackPointChanged(double value)
    {
        if (ActiveImage != null)
        {
            ActiveImage.BlackPoint = value;
            // Se il cambiamento è interno (cambio immagine), NON ricalcolare K
            if (!_isInternalChange) 
            {
                UpdateLockedSigmaFactors();
            }
        }
    }

    partial void OnWhitePointChanged(double value)
    {
        if (ActiveImage != null)
        {
            ActiveImage.WhitePoint = value;
            if (!_isInternalChange)
            {
                UpdateLockedSigmaFactors();
            }
        }
    }
    
    partial void OnSearchRadiusChanged(int value) => Viewport.SearchRadius = value;
    partial void OnTargetCoordinateChanged(Point? value) => Viewport.TargetCoordinate = value;
    
    partial void OnSelectedModeChanged(AlignmentMode value)
    {
        ResetAlignmentState();
        RefreshNavigationCommands();
        if (!IsIndexAccessible(_currentStackIndex)) _ = NavigateToImage(0); 
        UpdateStackCounterText();
    }
    
    partial void OnCurrentStateChanged(AlignmentState value)
    {
        RefreshNavigationCommands();
        UpdateStackCounterText();
    }
    
    private void ResetAlignmentState()
    {
        CurrentState = AlignmentState.Initial;
        foreach (var entry in CoordinateEntries) entry.Coordinate = null;
        TargetCoordinate = null;
        CalculateCentersCommand.NotifyCanExecuteChanged();
        ApplyAlignmentCommand.NotifyCanExecuteChanged();
    }
    
    private async Task ApplyOptimalStretchAsync()
    {
        if (ActiveImage == null) return;
        await ActiveImage.ResetThresholdsAsync();
        BlackPoint = ActiveImage.BlackPoint;
        WhitePoint = ActiveImage.WhitePoint;
    }
    
    private void UpdateReticleVisibilityForCurrentState()
    {
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
            MinSearchRadius = 0; MaxSearchRadius = 100;
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

        return SelectedMode switch
        {
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
        
        if (SelectedMode == AlignmentMode.Automatic)
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
    
    private void UpdateLockedSigmaFactors()
    {
        if (ActiveImage == null) return;
        var (mean, sigma) = ActiveImage.GetImageStatistics();
        _lockedKBlack = (BlackPoint - mean) / sigma;
        _lockedKWhite = (WhitePoint - mean) / sigma;
        _hasLockedThresholds = true;
    }
    
    #endregion
}