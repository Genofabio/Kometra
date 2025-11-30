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
    Initial,
    Calculating,
    ResultsReady,
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
    
    private bool _hasLockedThresholds = false;
    // K_black: Quante sigma dista il punto di nero dalla media?
    private double _lockedKBlack; 
    // K_white: Quante sigma dista il punto di bianco dalla media?
    private double _lockedKWhite;

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
    
    public string ZoomStatusText => $"{Viewport.Scale:P0}";

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
    [NotifyPropertyChangedFor(nameof(IsNavigationVisible))]
    private AlignmentMode _selectedMode = AlignmentMode.Automatic;
    
    public bool IsSearchRadiusVisible => SelectedMode != AlignmentMode.Automatic;

    // --- Stato Raggio di Ricerca (Logico) ---
    [ObservableProperty]
    private int _minSearchRadius;
    [ObservableProperty]
    private int _maxSearchRadius = 100;
    [ObservableProperty]
    private int _searchRadius = 100;

    // --- Stato Risultati e Visibilità UI ---
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
    public bool IsProcessingVisible => CurrentState == AlignmentState.Processing || 
                                       CurrentState == AlignmentState.Calculating;
    public string ProcessingStatusText
    {
        get
        {
            return CurrentState switch
            {
                AlignmentState.Calculating => "Calcolo centri in corso...",
                AlignmentState.Processing => "Allineamento in corso...",
                _ => "Elaborazione..."
            };
        }
    }
    public bool IsNavigationVisible
    {
        get
        {
            if (!IsStack) return false;
            if (CurrentState != AlignmentState.Initial) return true;
            return SelectedMode != AlignmentMode.Automatic;
        }
    }
    public bool IsCoordinateListVisible => CurrentState != AlignmentState.Initial;
    // --- Helper Pubblici ---
    public IEnumerable<AlignmentMode> AvailableAlignmentModes { get; private set; }
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
        
        _sourceData = sourceData;
        _currentStackIndex = 0;
        _totalStackCount = sourceData.Count;
        IsStack = _totalStackCount > 1;

        // --- CAMBIO 2: Popola la lista delle modalità disponibili ---
        if (IsStack)
        {
            // Se è uno stack, permetti tutto
            AvailableAlignmentModes = new[] 
            { 
                AlignmentMode.Automatic, 
                AlignmentMode.Guided, 
                AlignmentMode.Manual 
            };
        }
        else
        {
            // Se è una singola immagine, rimuovi 'Guided' che richiede start/end
            AvailableAlignmentModes = new[] 
            { 
                AlignmentMode.Automatic, 
                AlignmentMode.Manual 
            };
        }
        
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
                string? displayName = $"Immagine {i + 1}";
                double imageHeight = 0;
                if (_sourceData[i] != null)
                {
                    imageHeight = _sourceData[i]!.ImageSize.Height;
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
                    DisplayName = displayName,
                    Coordinate = null,
                    ImageHeight = imageHeight
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
        UpdateStackCounterText();

        ActiveImage?.UnloadData(); 

        try
        {
            var newModel = _sourceData[index];
            if (newModel == null) throw new Exception("Dati FITS nulli.");
            
            FitsImageData dataToShow = newModel;
                
            ActiveImage = new FitsRenderer(
                dataToShow, 
                _fitsService, 
                _processingService
            );

            // 1. Inizializza l'immagine (Carica Matrice e calcola Auto-Stretch locale)
            await ActiveImage.InitializeAsync();
            
            // 2. Ottieni le statistiche dell'immagine corrente (Media e Rumore)
            var (mean, sigma) = ActiveImage.GetImageStatistics();

            // 3. LOGICA DI ADATTAMENTO VISIVO
            if (!_hasLockedThresholds)
            {
                // === PRIMA IMMAGINE (Master) ===
                // Usiamo l'Auto-Stretch calcolato da InitializeAsync come riferimento.
                double masterBlack = ActiveImage.BlackPoint;
                double masterWhite = ActiveImage.WhitePoint;
                
                // Calcoliamo e salviamo i fattori RELATIVI (K)
                // Formula inversa: K = (Valore - Media) / Sigma
                _lockedKBlack = (masterBlack - mean) / sigma;
                _lockedKWhite = (masterWhite - mean) / sigma;
                
                _hasLockedThresholds = true;
                
                // Aggiorniamo le proprietà del VM per la UI
                BlackPoint = masterBlack;
                WhitePoint = masterWhite;

                // Reset visuale solo alla prima immagine
                Viewport.ImageSize = ActiveImage.ImageSize;
                Viewport.ResetView(); 
                OnPropertyChanged(nameof(ZoomStatusText));
            }
            else
            {
                // === IMMAGINI SUCCESSIVE (Slave) ===
                // Ignoriamo l'auto-stretch assoluto appena calcolato.
                // Ricostruiamo le soglie basandoci sulla media/sigma di QUESTA immagine
                // applicando però lo stesso "stretch factor" (K) della prima.
                
                // Formula diretta: Valore = Media + (K * Sigma)
                double adaptedBlack = mean + (_lockedKBlack * sigma);
                double adaptedWhite = mean + (_lockedKWhite * sigma);

                // Applichiamo i valori adattati
                ActiveImage.BlackPoint = adaptedBlack;
                ActiveImage.WhitePoint = adaptedWhite;
                
                // Aggiorniamo le proprietà del VM
                BlackPoint = adaptedBlack;
                WhitePoint = adaptedWhite;
            }
            
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

        // 1. Ricalcola l'Auto-Stretch standard su QUESTA immagine
        // (Usa i percentili 15% - 99.8% definiti in FitsRenderer/ImageProcessingService)
        await ApplyOptimalStretchAsync();
        
        // 2. Ricalcola i nuovi fattori K basandosi sulle statistiche attuali
        // Questi diventano il nuovo "standard" per le prossime immagini
        var (mean, sigma) = ActiveImage.GetImageStatistics();
        
        _lockedKBlack = (ActiveImage.BlackPoint - mean) / sigma;
        _lockedKWhite = (ActiveImage.WhitePoint - mean) / sigma;
        
        _hasLockedThresholds = true; 
    }
    
    private bool CanResetThresholds() => ActiveImage != null;
    
    // --- Comandi Navigazione Stack ---
    
    private async Task NavigateToImage(int newIndex)
    {
        if (newIndex < 0 || newIndex >= _totalStackCount || newIndex == _currentStackIndex)
            return;
        
        TargetCoordinate = null; 
        await LoadStackImageAtIndexAsync(newIndex);
    }

    // Cerca l'indice precedente valido
    private int? GetPreviousAccessibleIndex()
    {
        for (int i = _currentStackIndex - 1; i >= 0; i--)
        {
            if (IsIndexAccessible(i)) return i;
        }
        return null;
    }

// Cerca l'indice successivo valido
    private int? GetNextAccessibleIndex()
    {
        for (int i = _currentStackIndex + 1; i < _totalStackCount; i++)
        {
            if (IsIndexAccessible(i)) return i;
        }
        return null;
    }

    private bool CanShowPrevious() => IsStack && GetPreviousAccessibleIndex().HasValue;
    private bool CanShowNext() => IsStack && GetNextAccessibleIndex().HasValue;

    [RelayCommand(CanExecute = nameof(CanShowPrevious))]
    private async Task PreviousImage()
    {
        var prevIndex = GetPreviousAccessibleIndex();
        if (prevIndex.HasValue)
        {
            await NavigateToImage(prevIndex.Value);
        }
    }

    [RelayCommand(CanExecute = nameof(CanShowNext))]
    private async Task NextImage()
    {
        var nextIndex = GetNextAccessibleIndex();
        if (nextIndex.HasValue)
        {
            await NavigateToImage(nextIndex.Value);
        }
    }
    
    [RelayCommand(CanExecute = nameof(CanShowPrevious))] 
    private async Task GoToFirstImage() => await NavigateToImage(0);

    [RelayCommand(CanExecute = nameof(CanShowNext))]
    private async Task GoToLastImage() => await NavigateToImage(_totalStackCount - 1);
    
    // --- Comandi Allineamento ---
    [RelayCommand(CanExecute = nameof(CanCalculateCenters))]
    private async Task CalculateCenters()
    {
        Debug.WriteLine($"[AlgnVM] Avvio calcolo centri (Modo: {SelectedMode})...");

        // 1. CAMBIO STATO: Nasconde il bottone, mostra la barra, imposta il testo
        CurrentState = AlignmentState.Calculating; 

        try
        {
            var currentCoords = CoordinateEntries.Select(e => e.Coordinate);

            // Esegui il lavoro pesante
            var newCoords = await _alignmentService.CalculateCentersAsync(
                SelectedMode, 
                CenteringMethod.LocalRegion,
                _sourceData, 
                currentCoords, 
                SearchRadius
            );

            // Aggiorna UI
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

            // 2. FINE: Mostra i risultati e i pulsanti Applica/Annulla
            CurrentState = AlignmentState.ResultsReady;
            
            UpdateReticleVisibilityForCurrentState();
            ApplyAlignmentCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Errore calcolo: {ex}");
            // In caso di errore, torna allo stato iniziale
            CurrentState = AlignmentState.Initial;
        }
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
            FinalProcessedData = (await _alignmentService.ApplyCenteringAsync(_sourceData, centers))!;

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
        OnPropertyChanged(nameof(ZoomStatusText));
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
        if (ActiveImage != null)
        {
            // 1. Aggiorna il renderer visivo
            ActiveImage.BlackPoint = value;
            
            // 2. IMPORTANTE: Aggiorna la "ricetta" di lock in tempo reale
            UpdateLockedSigmaFactors();
        }
    }
    
    partial void OnWhitePointChanged(double value)
    {
        if (ActiveImage != null)
        {
            // 1. Aggiorna il renderer visivo
            ActiveImage.WhitePoint = value;
            
            // 2. IMPORTANTE: Aggiorna la "ricetta" di lock in tempo reale
            UpdateLockedSigmaFactors();
        }
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
        
        PreviousImageCommand.NotifyCanExecuteChanged();
        NextImageCommand.NotifyCanExecuteChanged();
        GoToFirstImageCommand.NotifyCanExecuteChanged();
        GoToLastImageCommand.NotifyCanExecuteChanged();
        
        if (!IsIndexAccessible(_currentStackIndex))
        {
            _ = NavigateToImage(0); 
        }
        
        UpdateStackCounterText();
    }
    
    partial void OnCurrentStateChanged(AlignmentState value)
    {
        PreviousImageCommand.NotifyCanExecuteChanged();
        NextImageCommand.NotifyCanExecuteChanged();
        GoToFirstImageCommand.NotifyCanExecuteChanged();
        GoToLastImageCommand.NotifyCanExecuteChanged();
        UpdateStackCounterText();
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
    
    /// <summary>
    /// Determina se un indice è visibile in base alla modalità e allo stato corrente.
    /// </summary>
    private bool IsIndexAccessible(int index)
    {
        if (CurrentState != AlignmentState.Initial) return true;
        
        if (index < 0 || index >= _totalStackCount) return false;

        switch (SelectedMode)
        {
            case AlignmentMode.Automatic:
                return index == 0;

            case AlignmentMode.Guided:
                return index == 0 || index == _totalStackCount - 1;

            case AlignmentMode.Manual:
            default:
                return true;
        }
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
            // Se siamo sulla prima immagine (indice 0) è lo step 1.
            // Altrimenti (siamo sull'ultima) è lo step 2.
            int visibleStep = (_currentStackIndex == 0) ? 1 : 2;
            StackCounterText = $"{visibleStep} / 2";
        }
    }
    
    /// <summary>
    /// Ricalcola i fattori Sigma (K) basandosi sui valori correnti di Black/White Point
    /// e sulle statistiche dell'immagine attuale.
    /// </summary>
    private void UpdateLockedSigmaFactors()
    {
        // Se non c'è immagine o non è ancora pronta, usciamo
        if (ActiveImage == null) return;
        
        // Non ricalcoliamo se stiamo caricando l'immagine (evitiamo loop inutili)
        // anche se matematicamente sarebbe innocuo.
        // Nota: Puoi gestire questo con un flag _isLoading se necessario, 
        // ma generalmente il calcolo è così veloce che non serve.

        var (mean, sigma) = ActiveImage.GetImageStatistics();
        
        // Aggiorniamo la "distanza relativa" dal background.
        // Da ora in poi, questa è la nuova configurazione che verrà applicata alle prossime immagini.
        _lockedKBlack = (BlackPoint - mean) / sigma;
        _lockedKWhite = (WhitePoint - mean) / sigma;
        
        _hasLockedThresholds = true;
    }
    
    #endregion
}