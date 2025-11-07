using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KomaLab.Models;
using KomaLab.Services;
using System.Diagnostics;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace KomaLab.ViewModels;

// --- ENUM PER LE MODALITÀ ---
public enum AlignmentMode
{
    Automatic,
    Guided,
    Manual
}

/// <summary>
/// ViewModel per la finestra di Allineamento.
/// </summary>
public partial class AlignmentViewModel : ObservableObject
{
    // --- Classe Helper per la Lista delle Coordinate ---
    
    /// <summary>
    /// Rappresenta una singola riga nella lista delle coordinate.
    /// </summary>
    public partial class CoordinateEntry : ObservableObject
    {
        public int Index { get; set; }
        public string DisplayName { get; set; } = "";
        public int DisplayIndex => Index + 1;
        
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CoordinateString))] // Notifica la stringa
        private Point? _coordinate;

        // Proprietà helper per il formatting
        public string CoordinateString => Coordinate.HasValue
            ? $"({Coordinate.Value.X:F2}; {Coordinate.Value.Y:F2})"
            : "---";
    }
    
    // --- Campi ---
    private readonly IFitsService _fitsService;
    private readonly BaseNodeViewModel _nodeToAlign; 
    private MultipleImagesNodeModel? _stackModel;
    private List<string>? _imagePaths;
    private int _currentStackIndex = 0;
    private int _totalStackCount = 0;
    
    // --- FIX: Flag per sbloccare il pulsante Applica ---
    private bool _centersHaveBeenCalculated = false;
    
    // --- NUOVA SOURCE OF TRUTH per le coordinate ---
    public ObservableCollection<CoordinateEntry> CoordinateEntries { get; } = new();
    
    // --- Proprietà (Pan/Zoom, Immagine, Soglie) ---
    public Size ViewportSize { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(TargetMarkerScreenX))] [NotifyPropertyChangedFor(nameof(TargetMarkerScreenY))]
    private double _offsetX = 0;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(TargetMarkerScreenX))] [NotifyPropertyChangedFor(nameof(TargetMarkerScreenY))]
    private double _offsetY = 0;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(TargetMarkerScreenX))] [NotifyPropertyChangedFor(nameof(TargetMarkerScreenY))]
    private double _scale = 1.0; 
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CorrectImageSize))]
    private FitsDisplayViewModel? _activeImage; 
    public Size CorrectImageSize => ActiveImage?.ImageSize ?? default;
    [ObservableProperty] private double _blackPoint;
    [ObservableProperty] private double _whitePoint;

    // --- Proprietà per la Selezione (Mirino) ---
    public bool IsTargetMarkerVisible => TargetCoordinate.HasValue;
    public double TargetMarkerScreenX => TargetCoordinate.HasValue ? (TargetCoordinate.Value.X * Scale) + OffsetX : 0;
    public double TargetMarkerScreenY => TargetCoordinate.HasValue ? (TargetCoordinate.Value.Y * Scale) + OffsetY : 0;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(TargetMarkerScreenX))] [NotifyPropertyChangedFor(nameof(TargetMarkerScreenY))] [NotifyPropertyChangedFor(nameof(IsTargetMarkerVisible))]
    private Point? _targetCoordinate;

    // --- Proprietà per la modalità Stack ---
    [ObservableProperty] private bool _isStack = false;
    [ObservableProperty] private string _stackCounterText = "";

    // --- Proprietà per la Modalità ---
    [ObservableProperty]
    private AlignmentMode _selectedMode = AlignmentMode.Automatic;
    
    // --- FIX: Aggiunte proprietà per il Raggio di Ricerca (ora int) ---
    [ObservableProperty]
    private int _minSearchRadius = 0; // Minimo raggio (es. 5px)

    [ObservableProperty]
    private int _maxSearchRadius = 100; // Massimo di default (verrà aggiornato)

    [ObservableProperty]
    private int _searchRadius = 25; // Valore di default (es. 25px)
    // --- FINE FIX ---
    
    partial void OnSearchRadiusChanged(int value)
    {
        UpdateSearchBoxPosition();
    }
    
    // --- FIX: Proprietà per il riquadro di ricerca (pixel schermo) ---
    [ObservableProperty]
    private double _searchBoxLeft;
    [ObservableProperty]
    private double _searchBoxTop;
    [ObservableProperty]
    private double _searchBoxWidth;
    [ObservableProperty]
    private double _searchBoxHeight;
    // --- FINE FIX ---
    
    [ObservableProperty]
    private bool _isCoordinateListVisible;
    
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCalculateButtonVisible))]
    [NotifyPropertyChangedFor(nameof(IsApplyCancelButtonsVisible))]
    private bool _areResultsAvailable = false;

    public bool IsCalculateButtonVisible => !AreResultsAvailable;
    public bool IsApplyCancelButtonsVisible => AreResultsAvailable;
    
    // Proprietà helper per il binding della ComboBox
    public static IEnumerable<AlignmentMode> AlignmentModes => Enum.GetValues<AlignmentMode>();

    // Task per segnalare che l'immagine è caricata
    public TaskCompletionSource ImageLoadedTcs { get; } = new();

    // --- Costruttore ---
    public AlignmentViewModel(BaseNodeViewModel nodeToAlign, IFitsService fitsService)
    {
        _nodeToAlign = nodeToAlign;
        _fitsService = fitsService;
        _ = InitializeFromNodeAsync();
    }

    // --- Logica di Inizializzazione ---
    private async Task InitializeFromNodeAsync()
    {
        try
        {
            IsStack = false; 
            string? singlePath = null;
            
            if (_nodeToAlign is SingleImageNodeViewModel singleNode)
            {
                // 1. Ottieni il percorso dell'immagine
                var modelField = typeof(BaseNodeViewModel).GetField("Model", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (modelField?.GetValue(singleNode) is SingleImageNodeModel singleModel)
                {
                    singlePath = singleModel.ImagePath;
                }
                
                if (string.IsNullOrEmpty(singlePath))
                {
                    throw new InvalidOperationException("Impossibile trovare il percorso per l'immagine singola.");
                }

                // --- MODIFICA CONSIGLIATA ---
                // 2. Carica l'immagine come un NUOVO oggetto (come fai per lo stack).
                //    Questo crea un "sandbox" e non modifica il nodo originale.
                try
                {
                    var imageData = await _fitsService.LoadFitsFromFileAsync(singlePath);
                    if (imageData == null) throw new Exception("Dati FITS nulli.");

                    ActiveImage = new FitsDisplayViewModel(imageData, _fitsService);
                    ActiveImage.Initialize();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[AlgnVM] Errore caricamento immagine singola: {ex}");
                    ActiveImage = null;
                }
                // --- FINE MODIFICA ---
                
                _currentStackIndex = 0;
                _totalStackCount = 1; 
            }
            else if (_nodeToAlign is MultipleImagesNodeViewModel stackNode)
            {
                var modelField = typeof(BaseNodeViewModel).GetField("Model", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (modelField?.GetValue(stackNode) is not MultipleImagesNodeModel stackModel)
                {
                     throw new InvalidCastException("Impossibile accedere al modello dati del nodo multiplo.");
                }
                _stackModel = stackModel;
                _imagePaths = _stackModel.ImagePaths;
                _totalStackCount = _imagePaths.Count;
                _currentStackIndex = stackNode.CurrentIndex;
                IsStack = true;
            }
            else
            {
                throw new NotSupportedException($"Tipo di nodo '{_nodeToAlign.GetType().Name}' non supportato.");
            }
            
            // --- Popola la NUOVA collection ---
            CoordinateEntries.Clear();
            if (_imagePaths != null) // Caso Stack
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
                await LoadStackImageAtIndexAsync(_currentStackIndex);
            }
            else if (singlePath != null) // Caso Singolo
            {
                 CoordinateEntries.Add(new CoordinateEntry
                 {
                     Index = 0,
                     DisplayName = System.IO.Path.GetFileName(singlePath),
                     Coordinate = null
                 });
                 UpdateReticleVisibilityForCurrentState();
            }

            if (ActiveImage != null)
            {
                await ApplyOptimalStretchAsync();
                OnPropertyChanged(nameof(CorrectImageSize));
                ResetThresholdsCommand.NotifyCanExecuteChanged();
            }
        }
        catch (System.Exception ex)
        {
            Debug.WriteLine($"--- CRASH IN AlgnVM.InitializeFromNodeAsync --- {ex}");
        }
        finally
        {
            ImageLoadedTcs.SetResult();
            UpdateCoordinateListVisibility();
            CalculateCentersCommand.NotifyCanExecuteChanged();
            ApplyAlignmentCommand.NotifyCanExecuteChanged();
            
            // --- FIX: Notifica comandi di navigazione ---
            PreviousImageCommand.NotifyCanExecuteChanged();
            NextImageCommand.NotifyCanExecuteChanged();
            GoToFirstImageCommand.NotifyCanExecuteChanged();
            GoToLastImageCommand.NotifyCanExecuteChanged();
            // --- FINE FIX ---
        }
    }

    /// <summary>
    /// Carica un'immagine specifica dallo stack.
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
            
            UpdateSearchRadiusRange();
            
            await ApplyOptimalStretchAsync();
            UpdateReticleVisibilityForCurrentState();
            
            UpdateSearchBoxPosition();

            PreviousImageCommand.NotifyCanExecuteChanged();
            NextImageCommand.NotifyCanExecuteChanged();
            // --- FIX: Notifica nuovi comandi ---
            GoToFirstImageCommand.NotifyCanExecuteChanged();
            GoToLastImageCommand.NotifyCanExecuteChanged();
            // --- FINE FIX ---
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AlgnVM] Errore caricamento immagine stack: {ex}");
            ActiveImage = null;
        }
    }
    
    // --- Metodi Parziali ---
    
    partial void OnBlackPointChanged(double value)
    {
        if (ActiveImage != null) ActiveImage.BlackPoint = value;
    }
    
    partial void OnWhitePointChanged(double value)
    {
        if (ActiveImage != null) ActiveImage.WhitePoint = value;
    }

    /// <summary>
    /// Chiamato quando l'utente cambia RadioButton.
    /// Azzera tutte le coordinate salvate.
    /// </summary>
    partial void OnSelectedModeChanged(AlignmentMode value)
    {
        Debug.WriteLine($"[AlgnVM] Modalità cambiata: {value}. Reset coordinate.");
        
        // --- FIX: Usa il metodo di reset centralizzato ---
        ResetAlignmentState();
        // --- FINE FIX ---
    }
    
    /// <summary>
    /// Aggiorna la visibilità della lista coordinate in base alla modalità.
    /// </summary>
    private void UpdateCoordinateListVisibility()
    {
        // La lista è SEMPRE nascosta all'inizio o al cambio di modalità.
        // Verrà mostrata da 'CalculateCenters' o 'SetTargetCoordinate'.
        IsCoordinateListVisible = false;
    }
    
    /// <summary>
    /// Metodo helper per decidere se il mirino deve essere visibile.
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
            switch (SelectedMode)
            {
                case AlignmentMode.Manual:
                case AlignmentMode.Guided: // <<< MODIFICATO (rimosso il blocco 'if' da qui)
                case AlignmentMode.Automatic:
                    shouldBeVisible = true; 
                    break;
            }
        }
        
        TargetCoordinate = shouldBeVisible ? currentEntry.Coordinate : null;
    }

    // --- Comandi (Zoom, Pan, Reset) ---
    [RelayCommand] private void ZoomIn() 
    {
        var center = new Point(ViewportSize.Width / 2, ViewportSize.Height / 2);
        ApplyZoomAtPoint(1.25, center);
    }
    [RelayCommand] private void ZoomOut() 
    {
        var center = new Point(ViewportSize.Width / 2, ViewportSize.Height / 2);
        ApplyZoomAtPoint(1.0 / 1.25, center);
    }
    [RelayCommand] private void ResetView() 
    {
        if (CorrectImageSize.Width == 0 || CorrectImageSize.Height == 0 || ViewportSize.Width == 0)
        {
            Scale = 1.0; OffsetX = 0; OffsetY = 0; return;
        }
        double padding = 0.9; 
        double scaleX = (ViewportSize.Width * padding) / CorrectImageSize.Width;
        double scaleY = (ViewportSize.Height * padding) / CorrectImageSize.Height;
        Scale = Math.Min(scaleX, scaleY);
        OffsetX = (ViewportSize.Width - (CorrectImageSize.Width * Scale)) / 2;
        OffsetY = (ViewportSize.Height - (CorrectImageSize.Height * Scale)) / 2;
        UpdateTargetMarkerPosition();
    }
    [RelayCommand(CanExecute = nameof(CanResetThresholds))]
    private async Task ResetThresholds() => await ApplyOptimalStretchAsync();
    private bool CanResetThresholds() => ActiveImage != null;
    private async Task ApplyOptimalStretchAsync()
    {
        if (ActiveImage == null) return;
        var (newBlack, newWhite) = await ActiveImage.ResetThresholdsAsync();
        BlackPoint = newBlack;
        WhitePoint = newWhite;
    }

    // --- Comandi Navigazione Stack ---
    [RelayCommand(CanExecute = nameof(CanShowPrevious))]
    private async Task PreviousImage()
    {
        if (!CanShowPrevious()) return; 
        TargetCoordinate = null; 
        await LoadStackImageAtIndexAsync(_currentStackIndex - 1);
    }
    private bool CanShowPrevious() => IsStack && _currentStackIndex > 0;

    [RelayCommand(CanExecute = nameof(CanShowNext))]
    private async Task NextImage()
    {
        if (!CanShowNext()) return; 
        TargetCoordinate = null; 
        await LoadStackImageAtIndexAsync(_currentStackIndex + 1);
    }
    private bool CanShowNext() => IsStack && _currentStackIndex < _totalStackCount - 1;
    
    // --- FIX: Aggiunti comandi per Prima/Ultima immagine ---
    [RelayCommand(CanExecute = nameof(CanGoToFirst))]
    private async Task GoToFirstImage()
    {
        if (!CanGoToFirst()) return;
        TargetCoordinate = null;
        await LoadStackImageAtIndexAsync(0);
    }
    private bool CanGoToFirst() => IsStack && _currentStackIndex > 0;

    [RelayCommand(CanExecute = nameof(CanGoToLast))]
    private async Task GoToLastImage()
    {
        if (!CanGoToLast()) return;
        TargetCoordinate = null;
        await LoadStackImageAtIndexAsync(_totalStackCount - 1);
    }
    private bool CanGoToLast() => IsStack && _currentStackIndex < _totalStackCount - 1;
    // --- FINE FIX ---

    // --- *** NUOVI COMANDI DI ALLINEAMENTO *** ---
    
    [RelayCommand(CanExecute = nameof(CanCalculateCenters))]
    private async Task CalculateCenters()
    {
        Debug.WriteLine($"[AlgnVM] Avvio calcolo centri (Modo: {SelectedMode})...");
        
        // --- SIMULAZIONE ALGORITMO ---
        await Task.Delay(500); // Simula lavoro
        
        Random rand = new Random();

        // --- LOGICA DI CALCOLO DIFFERENZIATA ---
        if (SelectedMode == AlignmentMode.Automatic)
        {
            Debug.WriteLine("[AlgnVM] Calcolo Automatico: sovrascrivo tutto.");
            foreach (var entry in CoordinateEntries)
            {
                // Simula un punto trovato
                double x = rand.NextDouble() * CorrectImageSize.Width;
                double y = rand.NextDouble() * CorrectImageSize.Height;
                entry.Coordinate = new Point(x, y);
            }
        }
        else if (SelectedMode == AlignmentMode.Guided)
        {
            Debug.WriteLine("[AlgnVM] Calcolo Guidato: interpolo i mancanti.");
            // (Qui andrebbe la logica di interpolazione)
            // Per ora, simulo il riempimento solo dei punti mancanti
            foreach (var entry in CoordinateEntries.Where(e => e.Coordinate == null))
            {
                double x = rand.NextDouble() * CorrectImageSize.Width;
                double y = rand.NextDouble() * CorrectImageSize.Height;
                entry.Coordinate = new Point(x, y);
            }
        }
        else if (SelectedMode == AlignmentMode.Manual)
        {
             Debug.WriteLine("[AlgnVM] Calcolo Manuale: nessun calcolo, solo 'sblocco'.");
             // Non fare nulla, i punti sono già stati impostati dall'utente.
             // Il pulsante è stato premuto solo per confermare.
        }
        // --- FINE LOGICA DIFFERENZIATA ---
        
        
        Debug.WriteLine("[AlgnVM] Calcolo completato.");
        
        AreResultsAvailable = true;
        
        // Mostra la lista
        IsCoordinateListVisible = true;
        
        // Aggiorna la vista corrente e abilita "Applica"
        UpdateReticleVisibilityForCurrentState();
        ApplyAlignmentCommand.NotifyCanExecuteChanged();
    }
    
    private bool CanCalculateCenters()
    {
        // --- LOGICA MODIFICATA PER BUG 2 (Guided) ---
        switch (SelectedMode)
        {
            case AlignmentMode.Automatic:
                // 1. Sempre abilitato
                return true; 

            case AlignmentMode.Guided:
                if (CoordinateEntries.Count == 0)
                    return false; 

                // --- FIX: Gestione Immagine Singola (totalStackCount == 1) ---
                if (_totalStackCount == 1)
                {
                    // Per immagine singola, basta che l'unica coordinata (Index 0) sia impostata
                    return CoordinateEntries.FirstOrDefault(e => e.Index == 0)?.Coordinate.HasValue == true;
                }
                // --- FINE FIX ---

                // Logica standard per Stack (totalStackCount > 1)
                // 2. Abilitato se la prima E l'ultima immagine hanno coordinate
                var first = CoordinateEntries.FirstOrDefault(e => e.Index == 0);
                var last = CoordinateEntries.FirstOrDefault(e => e.Index == _totalStackCount - 1);
                bool hasFirstAndLast = first?.Coordinate.HasValue == true && last?.Coordinate.HasValue == true;

                // 3. Abilitato se TUTTE le immagini hanno coordinate
                bool hasAllGuided = CoordinateEntries.All(e => e.Coordinate.HasValue);

                return hasFirstAndLast || hasAllGuided;

            case AlignmentMode.Manual:
                // Abilitato se TUTTE le immagini hanno coordinate
                if (CoordinateEntries.Count == 0)
                    return false;
                
                bool hasAllManual = CoordinateEntries.All(e => e.Coordinate.HasValue);
                return hasAllManual;

            default:
                return false;
        }
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
            
        // --- FIX: Usa la nuova proprietà di stato ---
        bool allCoordinatesSet = CoordinateEntries.All(e => e.Coordinate.HasValue);
        return allCoordinatesSet && AreResultsAvailable;
        // --- FINE FIX ---
    }

    // --- Metodi Pubblici per il Code-Behind ---
    
    public void ApplyZoomAtPoint(double scaleFactor, Point viewportZoomPoint)
    {
        double oldScale = Scale;
        double newScale = Math.Clamp(oldScale * scaleFactor, 0.01, 20);
        OffsetX = viewportZoomPoint.X - (viewportZoomPoint.X - OffsetX) * (newScale / oldScale);
        OffsetY = viewportZoomPoint.Y - (viewportZoomPoint.Y - OffsetY) * (newScale / oldScale);
        Scale = newScale;
        UpdateTargetMarkerPosition();
    }

    public void ApplyPan(double deltaX, double deltaY)
    {
        OffsetX += deltaX;
        OffsetY += deltaY;
        UpdateTargetMarkerPosition();
    }

    public void SetTargetCoordinate(Point imageCoordinate)
    {
        // --- FIX: Logica di blocco modificata ---
        if (!AreResultsAvailable) // Se i centri NON sono ancora stati calcolati
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
        // Se i centri SONO stati calcolati, o se sei in Manuale, o in Guidata (primo/ultimo),
        // il codice prosegue e permette la modifica.
        // --- FINE FIX ---

        TargetCoordinate = imageCoordinate; 
        
        UpdateTargetMarkerPosition();
        
        var entry = CoordinateEntries.FirstOrDefault(e => e.Index == _currentStackIndex);
        if (entry != null)
        {
            entry.Coordinate = imageCoordinate;
        }
        
        // --- FIX: Rimosso il reset del flag: le modifiche post-calcolo sono OK ---
        
        Debug.WriteLine($"[AlgnVM] Clic Preciso! Coordinata Immagine: {imageCoordinate}");
        
        ApplyAlignmentCommand.NotifyCanExecuteChanged();
        CalculateCentersCommand.NotifyCanExecuteChanged();
    }
 
    public void ClearTarget()
    {
        // --- FIX: Logica "Annulla" se si rimuove la croce POST-calcolo ---
        if (AreResultsAvailable)
        {
            // Se i risultati sono già calcolati, rimuovere una croce
            // equivale a "Annullare" l'intero calcolo.
            ResetAlignmentState();
            return;
        }
        // --- FINE FIX ---
        
        // --- Logica PRE-calcolo (rimasta invariata) ---
        // Se i centri NON sono ancora stati calcolati
        if (SelectedMode == AlignmentMode.Automatic) return;
        if (IsStack && SelectedMode == AlignmentMode.Guided)
        {
            if (_currentStackIndex != 0 && _currentStackIndex != (_totalStackCount - 1))
                return;
        }
        
        TargetCoordinate = null; 
        
        var entry = CoordinateEntries.FirstOrDefault(e => e.Index == _currentStackIndex);
        if (entry != null)
        {
            entry.Coordinate = null;
        }
        UpdateSearchBoxPosition();
        Debug.WriteLine("[AlgnVM] Mirino rimosso (pre-calcolo).");
        
        ApplyAlignmentCommand.NotifyCanExecuteChanged();
        CalculateCentersCommand.NotifyCanExecuteChanged();
    }
    
    public void UpdateTargetMarkerPosition()
    {
        OnPropertyChanged(nameof(TargetMarkerScreenX));
        OnPropertyChanged(nameof(TargetMarkerScreenY));
        UpdateSearchBoxPosition();
    }
    
    /// <summary>
    /// Resetta lo stato dell'allineamento ai valori pre-calcolo.
    /// Nasconde i risultati, resetta i flag e le coordinate.
    /// </summary>
    private void ResetAlignmentState()
    {
        Debug.WriteLine($"[AlgnVM] Resetting alignment state.");
        
        // 1. Resetta i flag (Pulsanti)
        AreResultsAvailable = false;
        
        // 3. Resetta le croci e le coordinate
        foreach (var entry in CoordinateEntries)
        {
            entry.Coordinate = null;
        }
        TargetCoordinate = null;
        
        UpdateSearchBoxPosition();
        
        // 2. Nasconde la lista coordinate
        UpdateCoordinateListVisibility(); 
        
        // 4 & 5 (Pulsanti) sono gestiti dal reset di AreResultsAvailable
        
        // Notifica i comandi
        CalculateCentersCommand.NotifyCanExecuteChanged();
        ApplyAlignmentCommand.NotifyCanExecuteChanged();
    }
    
    /// <summary>
    /// Aggiorna i limiti Min/Max dello slider del raggio di ricerca
    /// in base alla dimensione dell'immagine attiva.
    /// </summary>
    private void UpdateSearchRadiusRange()
    {
        if (ActiveImage == null || ActiveImage.ImageSize == default(Size))
        {
            // Mantiene i valori di default se non c'è immagine
            MinSearchRadius = 5;
            MaxSearchRadius = 100;
        }
        else
        {
            // Il raggio massimo è metà della dimensione più piccola
            double minDimension = Math.Min(ActiveImage.ImageSize.Width, ActiveImage.ImageSize.Height);
            MinSearchRadius = 5; 
            MaxSearchRadius = (int)Math.Floor(minDimension / 2.0); // Cast a int
        }

        // Assicura che il valore corrente sia nei nuovi limiti
        SearchRadius = Math.Clamp(SearchRadius, MinSearchRadius, MaxSearchRadius);
    }
    
    // --- FIX: Aggiunto metodo helper per aggiornare il riquadro ---
    private void UpdateSearchBoxPosition()
    {
        // Se non c'è un target o lo zoom è 0, nascondi il riquadro
        if (!TargetCoordinate.HasValue || Scale == 0)
        {
            SearchBoxLeft = 0;
            SearchBoxTop = 0;
            SearchBoxWidth = 0;
            SearchBoxHeight = 0;
            return;
        }

        // 1. Calcola la dimensione del riquadro (pixel schermo)
        // Il raggio è il "metà-lato", quindi il lato intero è Raggio * 2
        double halfSizeScreen = SearchRadius * Scale;
        double fullSizeScreen = halfSizeScreen * 2;
            
        SearchBoxWidth = fullSizeScreen;
        SearchBoxHeight = fullSizeScreen;

        // 2. Calcola la posizione del centro (pixel schermo)
        // (Questa è la stessa logica di TargetMarkerScreenX/Y)
        double centerXScreen = (TargetCoordinate.Value.X * Scale) + OffsetX;
        double centerYScreen = (TargetCoordinate.Value.Y * Scale) + OffsetY;

        // 3. Calcola l'angolo in alto a sinistra (Centro - Metà Dimensione)
        SearchBoxLeft = centerXScreen - halfSizeScreen;
        SearchBoxTop = centerYScreen - halfSizeScreen;
    }
}