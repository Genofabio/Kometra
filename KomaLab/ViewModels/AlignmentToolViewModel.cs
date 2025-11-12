using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KomaLab.Services;
using System.Diagnostics;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using KomaLab.ViewModels.Helpers;
using OpenCvSharp;
using CoordinateEntry = KomaLab.ViewModels.Helpers.CoordinateEntry;
using Point = Avalonia.Point;
using Size = Avalonia.Size; // Necessario per CoordinateEntry e ViewportManager

namespace KomaLab.ViewModels;

// --- ENUM PER LE MODALITÀ ---
public enum AlignmentMode
{
    Automatic,
    Guided,
    Manual
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
    private readonly BaseNodeViewModel _nodeToAlign; 
    private readonly IAlignmentService _alignmentService;
    
    private List<string>? _imagePaths;
    private int _currentStackIndex;
    private int _totalStackCount;
    
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
    private FitsDisplayViewModel? _activeImage; 
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
    private bool _areResultsAvailable;
    
    [ObservableProperty]
    private bool _isCoordinateListVisible;

    public bool IsCalculateButtonVisible => !AreResultsAvailable;
    public bool IsApplyCancelButtonsVisible => AreResultsAvailable;
    public bool IsSearchBoxVisible => IsTargetMarkerVisible && !AreResultsAvailable;
    public bool IsSearchRadiusControlsVisible => IsSearchRadiusVisible && !AreResultsAvailable;
    public bool IsRefinementMessageVisible => IsSearchRadiusVisible && AreResultsAvailable;
    
    // --- Helper Pubblici ---
    public static IEnumerable<AlignmentMode> AlignmentModes => Enum.GetValues<AlignmentMode>();
    public TaskCompletionSource ImageLoadedTcs { get; } = new();

    #endregion

    #region Costruttore e Inizializzazione

    public AlignmentToolViewModel(
        BaseNodeViewModel nodeToAlign, 
        IFitsService fitsService,
        IAlignmentService alignmentService) 
    {
        _nodeToAlign = nodeToAlign;
        _fitsService = fitsService;
        _alignmentService = alignmentService;
        
        _ = InitializeFromNodeAsync();
    }

    /// <summary>
    /// Inizializza lo stato del ViewModel in base al nodo da allineare.
    /// Prepara i percorsi e le voci delle coordinate.
    /// </summary>
    private async Task InitializeFromNodeAsync()
    {
        try
        {
            IsStack = false;
            
            if (_nodeToAlign is SingleImageNodeViewModel singleNode)
            {
                if (string.IsNullOrEmpty(singleNode.ImagePath))
                    throw new InvalidOperationException("Impossibile trovare il percorso per l'immagine singola.");
                
                _imagePaths = new List<string> { singleNode.ImagePath };
                _currentStackIndex = 0;
                _totalStackCount = 1; 
            }
            else if (_nodeToAlign is MultipleImagesNodeViewModel stackNode)
            {
                _imagePaths = stackNode.ImagePaths; 
                _totalStackCount = _imagePaths.Count;
                _currentStackIndex = stackNode.CurrentIndex;
                IsStack = true;
            }
            else
            {
                throw new NotSupportedException($"Tipo di nodo '{_nodeToAlign.GetType().Name}' non supportato.");
            }
            
            // Popola la collection
            CoordinateEntries.Clear();
            if (_imagePaths != null)
            {
                for (int i = 0; i < _totalStackCount; i++)
                {
                    CoordinateEntries.Add(new CoordinateEntry
                    {
                        Index = i,
                        DisplayName = System.IO.Path.GetFileName(_imagePaths[i]),
                        Coordinate = null
                    });
                }
            }
            
            // Delega il caricamento dell'immagine (sia singola che stack) al metodo unificato
            await LoadStackImageAtIndexAsync(_currentStackIndex);
            
            if (ActiveImage != null)
            {
                Viewport.ResetView();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"--- CRASH IN AlgnVM.InitializeFromNodeAsync --- {ex}");
        }
        finally
        {
            ImageLoadedTcs.SetResult();
            UpdateCoordinateListVisibility();
            CalculateCentersCommand.NotifyCanExecuteChanged();
            ApplyAlignmentCommand.NotifyCanExecuteChanged();
            
            // Notifica comandi di navigazione
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
        if (_imagePaths == null || index < 0 || index >= _totalStackCount) return;
        
        _currentStackIndex = index;
        StackCounterText = $"{_currentStackIndex + 1} / {_totalStackCount}";
        
        ActiveImage?.UnloadData(); 

        try
        {
            var imageData = await _fitsService.LoadFitsFromFileAsync(_imagePaths[_currentStackIndex]);
            if (imageData == null) throw new Exception("Dati FITS nulli.");

            ActiveImage = new FitsDisplayViewModel(imageData, _fitsService);
            ActiveImage.Initialize();
        
            // --- TEST FINALE (Modificato per usare solo Centroid) ---
            Debug.WriteLine("--- INIZIO TEST COMPLETO DI ALLINEAMENTO (Metodo Centroid) ---");

            // (Devi rendere 'LoadFitsDataAsMat' pubblico e aggiungerlo a IAlignmentService)
            using Mat originalMat = _alignmentService.LoadFitsDataAsMat(imageData); 

            // 2. Trova il centro (USANDO CENTROID)
            Point originalCenter = _alignmentService.GetCenterByCentroid(originalMat, sigma: 5.0);
            Debug.WriteLine($"Centro Originale (Centroid) (X, Y): {originalCenter.X:F2}, {originalCenter.Y:F2}");

            // 3. Sposta l'immagine (il metodo di shift è corretto)
            using Mat centeredMat = _alignmentService.CenterImageByCoords(originalMat, originalCenter);

            // 4. VERIFICA: Calcola il centroide della NUOVA immagine (USANDO CENTROID)
            Point newCenter = _alignmentService.GetCenterByCentroid(centeredMat, sigma: 5.0);
            Debug.WriteLine($"Centro Verificato (dopo shift) (X, Y): {newCenter.X:F2}, {newCenter.Y:F2}");
        
            Debug.WriteLine("--- FINE TEST COMPLETO ---");
            // --- FINE TEST ---

            Viewport.ImageSize = ActiveImage.ImageSize;
            // ... (resto del metodo) ...
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AlgnVM] Errore caricamento immagine stack: {ex}");
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
        
        // Delega il calcolo al servizio
        var newCoords = await _alignmentService.CalculateCentersAsync(
            SelectedMode, 
            currentCoords, 
            CorrectImageSize);
            
        // Aggiorna la UI con i risultati
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
        
        AreResultsAvailable = true;
        IsCoordinateListVisible = true;
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
    private void ApplyAlignment()
    {
        Debug.WriteLine("[AlgnVM] *** APPLICA ALLINEAMENTO ***");
        foreach (var entry in CoordinateEntries)
        {
            Debug.WriteLine($" - Img {entry.Index}: {entry.Coordinate}");
        }
        // TODO: Chiudere la finestra e passare i dati
    }
    
    [RelayCommand]
    private void CancelCalculation()
    {
        ResetAlignmentState();
    }
    
    private bool CanApplyAlignment()
    {
        if (CoordinateEntries.Count == 0 || CoordinateEntries.Count != _totalStackCount)
            return false;
            
        bool allCoordinatesSet = CoordinateEntries.All(e => e.Coordinate.HasValue);
        return allCoordinatesSet && AreResultsAvailable;
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
        // Blocco logica pre-calcolo
        if (!AreResultsAvailable) 
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
        
        // Applica coordinata
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
        // Logica Annulla post-calcolo
        if (AreResultsAvailable)
        {
            ResetAlignmentState();
            return;
        }
        
        // Logica pre-calcolo
        if (SelectedMode == AlignmentMode.Automatic) return;
        if (IsStack && SelectedMode == AlignmentMode.Guided)
        {
            if (_currentStackIndex != 0 && _currentStackIndex != (_totalStackCount - 1))
                return;
        }
        
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
        
        AreResultsAvailable = false;
        
        foreach (var entry in CoordinateEntries)
        {
            entry.Coordinate = null;
        }
        TargetCoordinate = null;
        
        UpdateCoordinateListVisibility(); 
        
        CalculateCentersCommand.NotifyCanExecuteChanged();
        ApplyAlignmentCommand.NotifyCanExecuteChanged();
    }
    
    private async Task ApplyOptimalStretchAsync()
    {
        if (ActiveImage == null) return;
        var (newBlack, newWhite) = await ActiveImage.ResetThresholdsAsync();
        BlackPoint = newBlack;
        WhitePoint = newWhite;
    }
    
    /// <summary>
    /// Aggiorna la visibilità della lista coordinate.
    /// </summary>
    private void UpdateCoordinateListVisibility()
    {
        IsCoordinateListVisible = false;
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
        bool shouldBeVisible = false; 

        if (currentEntry.Coordinate.HasValue)
        {
            // Il mirino è sempre visibile se una coordinata è impostata
            shouldBeVisible = true; 
        }
        
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