using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KomaLab.Services;
using System.Diagnostics;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using KomaLab.Models;
using KomaLab.ViewModels.Helpers;
using CoordinateEntry = KomaLab.ViewModels.Helpers.CoordinateEntry;
using Point = Avalonia.Point;
using Size = Avalonia.Size; 

namespace KomaLab.ViewModels;

// --- ENUM PER LE MODALITÀ ---
public enum AlignmentMode
{
    Automatic,
    Guided,
    Manual
}

// --- ENUM PER LO STATO ---
public enum AlignmentState
{
    /// <summary>
    /// Stato iniziale. L'utente sta impostando i parametri.
    /// </summary>
    Initial,
    
    /// <summary>
    /// Calcolo completato. I risultati sono pronti e l'utente può applicare.
    /// </summary>
    ResultsReady,
    
    /// <summary>
    /// Il pulsante "Applica" è stato premuto. Elaborazione in corso.
    /// </summary>
    Processing
}

/// <summary>
/// ViewModel per la finestra di Allineamento (Strumento di Allineamento).
/// Funziona come "Direttore d'orchestra" delegando la logica di
/// business (IAlignmentService) e la logica di visualizzazione (ViewportManager).
/// </summary>
public partial class AlignmentToolViewModel : ObservableObject
{
    #region Campi
    
    private readonly IFitsService _fitsService;
    private readonly IAlignmentService _alignmentService;
    private readonly IImageProcessingService _processingService;
    
    private readonly List<FitsImageData?> _sourceData; 
    private int _currentStackIndex;
    private readonly int _totalStackCount;
    
    private Size _viewportSize;

    #endregion

    #region Proprietà e Stato (per il Binding)

    // --- Gestori Delegati ---
    public ViewportManager Viewport { get; } = new();
    
    // --- Dati Principali ---
    public ObservableCollection<CoordinateEntry> CoordinateEntries { get; } = new();

    // --- Stato Viewport e Immagine ---
    public Size ViewportSize
    {
        get => _viewportSize;
        set
        {
            _viewportSize = value;
            Viewport.ViewportSize = value; // Collega al gestore
        }
    }
    
    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(CorrectImageSize))]
    private FitsRenderer? _activeImage; 
    public Size CorrectImageSize => ActiveImage?.ImageSize ?? default;
    
    [ObservableProperty] private double _blackPoint;
    [ObservableProperty] private double _whitePoint;

    // --- Stato Mirino (Logico) ---
    public bool IsTargetMarkerVisible => TargetCoordinate.HasValue;
    
    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(IsTargetMarkerVisible))]
    [NotifyPropertyChangedFor(nameof(IsSearchBoxVisible))]
    private Point? _targetCoordinate;

    // --- Stato Modalità Stack ---
    [ObservableProperty] private bool _isStack;
    [ObservableProperty] private string _stackCounterText = "";

    // --- Stato Modalità Allineamento ---
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSearchRadiusVisible))]
    [NotifyPropertyChangedFor(nameof(IsSearchRadiusControlsVisible))]
    [NotifyPropertyChangedFor(nameof(IsRefinementMessageVisible))]
    private AlignmentMode _selectedMode = AlignmentMode.Automatic;
    
    public bool IsSearchRadiusVisible => SelectedMode != AlignmentMode.Automatic;

    // --- Stato Raggio di Ricerca (Logico) ---
    [ObservableProperty]
    private int _minSearchRadius;
    [ObservableProperty]
    private int _maxSearchRadius = 100;
    [ObservableProperty]
    private int _searchRadius = 25;

    // --- Stato Risultati e Visibilità UI ---
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCalculateButtonVisible))]
    [NotifyPropertyChangedFor(nameof(IsApplyCancelButtonsVisible))]
    [NotifyPropertyChangedFor(nameof(IsSearchBoxVisible))]
    [NotifyPropertyChangedFor(nameof(IsSearchRadiusControlsVisible))]
    [NotifyPropertyChangedFor(nameof(IsRefinementMessageVisible))]
    [NotifyCanExecuteChangedFor(nameof(ApplyAlignmentCommand))]
    private AlignmentState _currentState = AlignmentState.Initial;
    
    /// <summary>
    /// Il risultato finale: una lista di nuovi dati FITS processati.
    /// </summary>
    public List<FitsImageData>? FinalProcessedData { get; private set; }
    
    public bool DialogResult { get; private set; }

    public event Action RequestClose;

    public bool IsCalculateButtonVisible => CurrentState == AlignmentState.Initial;
    public bool IsApplyCancelButtonsVisible => CurrentState == AlignmentState.ResultsReady;
    public bool IsSearchBoxVisible => IsTargetMarkerVisible && CurrentState == AlignmentState.Initial;
    public bool IsSearchRadiusControlsVisible => IsSearchRadiusVisible && CurrentState == AlignmentState.Initial;
    public bool IsRefinementMessageVisible => IsSearchRadiusVisible && CurrentState != AlignmentState.Initial;
    public bool IsCoordinateListVisible => CurrentState != AlignmentState.Initial;
    // --- Helper Pubblici ---
    public static IEnumerable<AlignmentMode> AlignmentModes => Enum.GetValues<AlignmentMode>();
    public TaskCompletionSource ImageLoadedTcs { get; } = new();

    #endregion

    #region Costruttore e Inizializzazione

    // --- MODIFICA IL COSTRUTTORE ---
    public AlignmentToolViewModel(
        List<FitsImageData?> sourceData,
        IFitsService fitsService,
        IAlignmentService alignmentService,
        IImageProcessingService processingService) 
    {
        _fitsService = fitsService;
        _alignmentService = alignmentService;
        _processingService = processingService;
        
        // Salva i dati
        _sourceData = sourceData;
        _currentStackIndex = 0; // Parte sempre dal primo
        _totalStackCount = sourceData.Count;
        IsStack = _totalStackCount > 1;
        
        Viewport.SearchRadius = this.SearchRadius;
        _ = InitializeAsync();
    }

    /// <summary>
    /// Inizializza lo stato del ViewModel in base al nodo da allineare.
    /// Prepara i percorsi e le voci delle coordinate.
    /// </summary>
    private async Task InitializeAsync()
    {
        try
        {
            // Popola la collection
            CoordinateEntries.Clear();
            for (int i = 0; i < _totalStackCount; i++)
            {
                // Cerca un titolo nel header
                string? displayName;
                if (_sourceData[i] != null)
                {
                    try
                    {
                        displayName = _sourceData[i]?.FitsHeader.GetStringValue("OBJECT");
                        if (string.IsNullOrWhiteSpace(displayName))
                            displayName = $"Immagine {i + 1}";
                    }
                    catch
                    {
                        Debug.WriteLine($"Impossibile leggere l'header 'OBJECT' per l'immagine {i}");
                    }
                }

                CoordinateEntries.Add(new CoordinateEntry
                {
                    Index = i,
                    Coordinate = null
                });
            }

            // Carica l'immagine iniziale
            await LoadStackImageAtIndexAsync(_currentStackIndex);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"--- CRASH IN AlgnVM.InitializeAsync --- {ex}");
        }
        finally
        {
            ImageLoadedTcs.SetResult();
            CalculateCentersCommand.NotifyCanExecuteChanged();
            ApplyAlignmentCommand.NotifyCanExecuteChanged();
            
            PreviousImageCommand.NotifyCanExecuteChanged();
            NextImageCommand.NotifyCanExecuteChanged();
            GoToFirstImageCommand.NotifyCanExecuteChanged();
            GoToLastImageCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>
    /// Carica un'immagine specifica (dallo stack o singola) all'indice dato.
    /// È l'unico metodo responsabile del caricamento di ActiveImage.
    /// </summary>
    private async Task LoadStackImageAtIndexAsync(int index)
    {
        if (index < 0 || index >= _totalStackCount) return;

        _currentStackIndex = index;
        StackCounterText = $"{_currentStackIndex + 1} / {_totalStackCount}";

        ActiveImage?.UnloadData(); 

        try
        {
            // --- MODIFICA ---
            // Non carica da disco, prende dalla lista in memoria
            var newModel = _sourceData[index];
            // --- FINE MODIFICA ---
            
            if (newModel == null) throw new Exception("Dati FITS nulli.");
            
            FitsImageData dataToShow = newModel;
                
            ActiveImage = new FitsRenderer(
                dataToShow, 
                _fitsService, 
                _processingService
            );
            await ActiveImage.InitializeAsync();
            
            BlackPoint = ActiveImage.BlackPoint;
            WhitePoint = ActiveImage.WhitePoint;
            
            Viewport.ImageSize = ActiveImage.ImageSize;
            Viewport.ResetView(); 
            OnPropertyChanged(nameof(CorrectImageSize)); 
            ResetThresholdsCommand.NotifyCanExecuteChanged();
            UpdateReticleVisibilityForCurrentState();
            UpdateSearchRadiusRange();
        
            PreviousImageCommand.NotifyCanExecuteChanged();
            NextImageCommand.NotifyCanExecuteChanged();
            GoToFirstImageCommand.NotifyCanExecuteChanged();
            GoToLastImageCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AlgnVM] Errore caricamento immagine stack: {ex.Message}");
            ActiveImage = null;
        }
    }

    #endregion

    #region Comandi

    // --- Comandi Viewport e Stretch Immagine ---
    [RelayCommand] private void ZoomIn() => Viewport.ZoomIn();
    [RelayCommand] private void ZoomOut() => Viewport.ZoomOut();
    [RelayCommand] private void ResetView() => Viewport.ResetView();
    
    [RelayCommand(CanExecute = nameof(CanResetThresholds))]
    private async Task ResetThresholds() => await ApplyOptimalStretchAsync();
    private bool CanResetThresholds() => ActiveImage != null;
    
    // --- Comandi Navigazione Stack ---
    private bool CanShowPrevious() => IsStack && _currentStackIndex > 0;
    private bool CanShowNext() => IsStack && _currentStackIndex < _totalStackCount - 1;
    
    private async Task NavigateToImage(int newIndex)
    {
        if (newIndex < 0 || newIndex >= _totalStackCount || newIndex == _currentStackIndex)
            return;
        
        TargetCoordinate = null; 
        await LoadStackImageAtIndexAsync(newIndex);
    }

    [RelayCommand(CanExecute = nameof(CanShowPrevious))]
    private async Task PreviousImage() => await NavigateToImage(_currentStackIndex - 1);

    [RelayCommand(CanExecute = nameof(CanShowNext))]
    private async Task NextImage() => await NavigateToImage(_currentStackIndex + 1);
    
    [RelayCommand(CanExecute = nameof(CanShowPrevious))] 
    private async Task GoToFirstImage() => await NavigateToImage(0);

    [RelayCommand(CanExecute = nameof(CanShowNext))]
    private async Task GoToLastImage() => await NavigateToImage(_totalStackCount - 1);
    
    // --- Comandi Allineamento ---
    [RelayCommand(CanExecute = nameof(CanCalculateCenters))]
    private async Task CalculateCenters()
    {
        Debug.WriteLine($"[AlgnVM] Avvio calcolo centri (Modo: {SelectedMode})...");

        var currentCoords = CoordinateEntries.Select(e => e.Coordinate);

        // 2. CHIEDI al servizio (con la nuova firma)
        var newCoords = await _alignmentService.CalculateCentersAsync(
            SelectedMode, 
            _sourceData, // <-- Passa i dati in memoria
            currentCoords, 
            SearchRadius
        );

        // 3. Aggiorna la UI (invariato)
        int i = 0;
        foreach (var coord in newCoords)
        {
            if (i < CoordinateEntries.Count)
            {
                CoordinateEntries[i].Coordinate = coord;
            }
            i++;
        }

        Debug.WriteLine("[AlgnVM] Calcolo completato.");

        CurrentState = AlignmentState.ResultsReady;
        UpdateReticleVisibilityForCurrentState();
        ApplyAlignmentCommand.NotifyCanExecuteChanged();
    }
    
    private bool CanCalculateCenters()
    {
        var currentCoords = CoordinateEntries.Select(e => e.Coordinate).ToList();
        // Delega la logica al servizio
        return _alignmentService.CanCalculate(
            SelectedMode, 
            currentCoords, 
            _totalStackCount);
    }

    [RelayCommand(CanExecute = nameof(CanApplyAlignment))]
    private async Task ApplyAlignment()
    {
        CurrentState = AlignmentState.Processing;
    
        try
        {
            // 1. Prendi i centri finali dalla UI
            var centers = CoordinateEntries.Select(e => e.Coordinate).ToList();

            // 2. Delega TUTTO il lavoro di processing al servizio
            FinalProcessedData = await _alignmentService.ApplyCenteringAsync(_sourceData, centers);

            DialogResult = true; // Successo
            RequestClose(); // Chiudi
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Processo di 'Applica' fallito: {ex.Message}");
            DialogResult = false;
            CurrentState = AlignmentState.Initial;
        }
    }
    
    [RelayCommand]
    private void CancelCalculation()
    {
        // Questo ora è corretto, resetta solo lo stato
        ResetAlignmentState();
    }
    
    private bool CanApplyAlignment()
    {
        return CurrentState == AlignmentState.ResultsReady;
    }

    #endregion
    
    #region Metodi Pubblici (per Code-Behind)

    public void ApplyZoomAtPoint(double scaleFactor, Point viewportZoomPoint)
    {
        Viewport.ApplyZoomAtPoint(scaleFactor, viewportZoomPoint);
    }

    public void ApplyPan(double deltaX, double deltaY)
    {
        Viewport.ApplyPan(deltaX, deltaY);
    }

    public void SetTargetCoordinate(Point imageCoordinate)
    {
        // 1. Blocco se l'applicazione è in corso (stato 'Processing')
        if (CurrentState == AlignmentState.Processing)
        {
            return; 
        }

        // 2. Blocco logica pre-calcolo (solo se CurrentState == Initial)
        //    Se CurrentState è ResultsReady, la logica è superata (si può raffinare)
        if (CurrentState == AlignmentState.Initial) 
        {
            if (SelectedMode == AlignmentMode.Automatic)
            {
                Debug.WriteLine("[AlgnVM] Clic ignorato (Modalità Automatica - usa 'Calcola').");
                return; 
            }

            if (IsStack && SelectedMode == AlignmentMode.Guided)
            {
                if (_currentStackIndex != 0 && _currentStackIndex != (_totalStackCount - 1))
                {
                    Debug.WriteLine($"[AlgnVM] Clic ignorato (Modalità Guidata). Index: {_currentStackIndex}");
                    return;
                }
            }
        }
    
        // 3. Applica coordinata
        TargetCoordinate = imageCoordinate; 
    
        if (_currentStackIndex >= 0 && _currentStackIndex < CoordinateEntries.Count)
        {
            CoordinateEntries[_currentStackIndex].Coordinate = imageCoordinate;
        }
    
        Debug.WriteLine($"[AlgnVM] Clic Preciso! Coordinata Immagine: {imageCoordinate}");
    
        ApplyAlignmentCommand.NotifyCanExecuteChanged();
        CalculateCentersCommand.NotifyCanExecuteChanged();
    }
 
    public void ClearTarget()
    {
        // 1. Logica Annulla post-calcolo: se ci sono risultati ('ResultsReady' o 'Processing'),
        //    il tasto destro/Clear resetta tutto allo stato 'Initial'.
        if (CurrentState != AlignmentState.Initial)
        {
            ResetAlignmentState();
            return;
        }
    
        // 2. Logica pre-calcolo (solo se CurrentState == Initial)
        if (SelectedMode == AlignmentMode.Automatic) return;
        if (IsStack && SelectedMode == AlignmentMode.Guided)
        {
            if (_currentStackIndex != 0 && _currentStackIndex != (_totalStackCount - 1))
                return;
        }
    
        // 3. Cancella il singolo target
        TargetCoordinate = null; 
        if (_currentStackIndex >= 0 && _currentStackIndex < CoordinateEntries.Count)
        {
            CoordinateEntries[_currentStackIndex].Coordinate = null;
        }
    
        Debug.WriteLine("[AlgnVM] Mirino rimosso (pre-calcolo).");
    
        ApplyAlignmentCommand.NotifyCanExecuteChanged();
        CalculateCentersCommand.NotifyCanExecuteChanged();
    }
    
    #endregion
    
    #region Metodi Privati e Parziali

    // --- Metodi Parziali (invocati da [ObservableProperty]) ---
    
    partial void OnBlackPointChanged(double value)
    {
        if (ActiveImage != null) ActiveImage.BlackPoint = value;
    }
    
    partial void OnWhitePointChanged(double value)
    {
        if (ActiveImage != null) ActiveImage.WhitePoint = value;
    }
    
    partial void OnSearchRadiusChanged(int value)
    {
        Viewport.SearchRadius = value;
    }
    
    partial void OnTargetCoordinateChanged(Point? value)
    {
        Viewport.TargetCoordinate = value;
    }
    
    partial void OnSelectedModeChanged(AlignmentMode value)
    {
        Debug.WriteLine($"[AlgnVM] Modalità cambiata: {value}. Reset coordinate.");
        ResetAlignmentState();
    }
    
    // --- Metodi Helper ---

    /// <summary>
    /// Resetta lo stato dell'allineamento ai valori pre-calcolo.
    /// </summary>
    private void ResetAlignmentState()
    {
        Debug.WriteLine($"[AlgnVM] Resetting alignment state.");
        
        CurrentState = AlignmentState.Initial;
        
        foreach (var entry in CoordinateEntries)
        {
            entry.Coordinate = null;
        }
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
    
    /// <summary>
    /// Sincronizza il mirino (TargetCoordinate) con il dato
    /// salvato per l'immagine corrente.
    /// </summary>
    private void UpdateReticleVisibilityForCurrentState()
    {
        if (_currentStackIndex < 0 || _currentStackIndex >= CoordinateEntries.Count)
        {
            TargetCoordinate = null;
            return;
        }
        
        var currentEntry = CoordinateEntries[_currentStackIndex];
        bool shouldBeVisible = currentEntry.Coordinate.HasValue;
        
        TargetCoordinate = shouldBeVisible ? currentEntry.Coordinate : null;
    }
    
    /// <summary>
    /// Aggiorna i limiti Min/Max dello slider del raggio di ricerca.
    /// </summary>
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
    
    #endregion
}