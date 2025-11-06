using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KomaLab.Models;
using KomaLab.Services;
using System.Diagnostics;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;

namespace KomaLab.ViewModels;

public partial class AlignmentViewModel : ObservableObject
{
    private readonly IFitsService _fitsService;
    private readonly BaseNodeViewModel _nodeToAlign; // Il nodo REALE

    // --- Campi per la modalità Stack (immagini multiple) ---
    private MultipleImagesNodeModel? _stackModel;
    private List<string>? _imagePaths;
    private int _currentStackIndex = 0;
    private int _totalStackCount = 0;
    
    // Dizionario per memorizzare la posizione del mirino per ogni immagine nello stack
    private readonly Dictionary<int, Point?> _stackCoordinates = new();
    
    // --- Proprietà per la Dimensione della Vista ---
    public Size ViewportSize { get; set; }

    // --- Proprietà per Pan & Zoom ---
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetMarkerScreenX))]
    [NotifyPropertyChangedFor(nameof(TargetMarkerScreenY))]
    private double _offsetX = 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetMarkerScreenX))]
    [NotifyPropertyChangedFor(nameof(TargetMarkerScreenY))]
    private double _offsetY = 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetMarkerScreenX))]
    [NotifyPropertyChangedFor(nameof(TargetMarkerScreenY))]
    // Rimossa notifica per ReticleStrokeThickness
    private double _scale = 1.0; 

    // --- Proprietà per l'Immagine ---
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CorrectImageSize))]
    private FitsDisplayViewModel? _activeImage; 
    
    public Size CorrectImageSize => ActiveImage?.ImageSize ?? default;
    
    // --- Proprietà per le Soglie ---
    [ObservableProperty]
    private double _blackPoint;
    [ObservableProperty]
    private double _whitePoint;

    // --- Proprietà per la Selezione (Mirino) ---
    
    // La visibilità ora dipende da TargetCoordinate
    public bool IsTargetMarkerVisible => _targetCoordinate.HasValue;
    
    public double TargetMarkerScreenX
    {
        get
        {
            if (TargetCoordinate.HasValue)
                return (TargetCoordinate.Value.X * Scale) + OffsetX;
            return 0;
        }
    }
    public double TargetMarkerScreenY
    {
        get
        {
            if (TargetCoordinate.HasValue)
                return (TargetCoordinate.Value.Y * Scale) + OffsetY;
            return 0;
        }
    }
    
    // RIMOSSA la proprietà ReticleStrokeThickness

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetMarkerScreenX))]
    [NotifyPropertyChangedFor(nameof(TargetMarkerScreenY))]
    [NotifyPropertyChangedFor(nameof(IsTargetMarkerVisible))]
    private Point? _targetCoordinate; // Il punto cliccato sull'immagine

    // --- Proprietà per la modalità Stack ---
    [ObservableProperty]
    private bool _isStack = false;

    [ObservableProperty]
    private string _stackCounterText = "";

    // Task per segnalare che l'immagine è caricata
    public TaskCompletionSource ImageLoadedTcs { get; } = new();

    // --- Costruttore ---
    public AlignmentViewModel(BaseNodeViewModel nodeToAlign, IFitsService fitsService)
    {
        _nodeToAlign = nodeToAlign;
        _fitsService = fitsService;
        
        // Carica l'immagine dal nodo, non un test
        _ = InitializeFromNodeAsync();
    }

    // --- Logica di Inizializzazione ---
    
    private async Task InitializeFromNodeAsync()
    {
        try
        {
            IsStack = false; // Default
            if (_nodeToAlign is SingleImageNodeViewModel singleNode)
            {
                // Il nodo potrebbe esistere ma non aver ancora caricato i suoi dati
                if (singleNode.FitsImage.ImageSize == default(Size))
                {
                    await singleNode.LoadDataAsync();
                }
                // Ora siamo sicuri che i dati ci sono (o il caricamento è fallito)
                ActiveImage = singleNode.FitsImage;

                if (ActiveImage != null)
                {
                    // Applica lo stretch automatico
                    await ApplyOptimalStretchAsync();
                    
                    OnPropertyChanged(nameof(CorrectImageSize));
                    ResetThresholdsCommand.NotifyCanExecuteChanged();
                }
            }
            else if (_nodeToAlign is MultipleImagesNodeViewModel stackNode)
            {
                // Cast al modello per ottenere l'elenco dei percorsi
                var modelField = typeof(BaseNodeViewModel).GetField("Model", 
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                
                if (modelField?.GetValue(stackNode) is not MultipleImagesNodeModel stackModel)
                {
                     throw new InvalidCastException("Impossibile accedere al modello dati del nodo multiplo.");
                }

                _stackModel = stackModel;
                _imagePaths = _stackModel.ImagePaths;
                _totalStackCount = _imagePaths.Count;
                _currentStackIndex = stackNode.CurrentIndex; // Inizia dall'indice corrente del nodo
                IsStack = true;

                // Carica l'immagine iniziale (il metodo applicherà lo stretch)
                await LoadStackImageAtIndexAsync(_currentStackIndex); 
                
                if (ActiveImage != null)
                {
                    OnPropertyChanged(nameof(CorrectImageSize));
                    ResetThresholdsCommand.NotifyCanExecuteChanged();
                }
            }
            else
            {
                throw new NotSupportedException($"Tipo di nodo '{_nodeToAlign.GetType().Name}' non supportato per l'allineamento.");
            }
        }
        catch (System.Exception ex)
        {
            Debug.WriteLine($"--- CRASH IN AlgnVM.InitializeFromNodeAsync --- {ex}");
        }
        finally
        {
            ImageLoadedTcs.SetResult(); // Sblocca il code-behind per il reset della vista
        }
    }

    /// <summary>
    /// Carica un'immagine specifica dallo stack.
    /// Questo metodo gestisce tutto: caricamento, stretch e ripristino del mirino.
    /// </summary>
    private async Task LoadStackImageAtIndexAsync(int index)
    {
        if (_imagePaths == null || index < 0 || index >= _totalStackCount) return;
        
        _currentStackIndex = index;
        StackCounterText = $"{_currentStackIndex + 1} / {_totalStackCount}";
        
        // Pulisce il mirino PRIMA di iniziare il caricamento
        TargetCoordinate = null; 
        
        // Rimuove la bitmap precedente per liberare memoria
        ActiveImage?.UnloadData(); 

        try
        {
            var imageData = await _fitsService.LoadFitsFromFileAsync(_imagePaths[_currentStackIndex]);
            if (imageData == null) throw new Exception("Dati FITS nulli.");

            ActiveImage = new FitsDisplayViewModel(imageData, _fitsService);
            ActiveImage.Initialize();
            
            // Applica lo stretch automatico OGNI VOLTA
            await ApplyOptimalStretchAsync();

            // Carica la coordinata (mirino) salvata per questa immagine
            if (_stackCoordinates.TryGetValue(index, out Point? savedCoordinate))
            {
                TargetCoordinate = savedCoordinate;
            }
            // else: TargetCoordinate resta null (impostato sopra)

            // Notifica i comandi e la UI
            PreviousImageCommand.NotifyCanExecuteChanged();
            NextImageCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AlgnVM] Errore caricamento immagine stack: {ex}");
            ActiveImage = null; // Mostra nulla in caso di errore
        }
    }
    
    // --- Metodi Parziali per le Soglie ---
    
    partial void OnBlackPointChanged(double value)
    {
        if (ActiveImage != null)
            ActiveImage.BlackPoint = value;
    }
    
    partial void OnWhitePointChanged(double value)
    {
        if (ActiveImage != null)
            ActiveImage.WhitePoint = value;
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
            Scale = 1.0;
            OffsetX = 0;
            OffsetY = 0;
            return;
        }

        double padding = 0.9; 
        double scaleX = (ViewportSize.Width * padding) / CorrectImageSize.Width;
        double scaleY = (ViewportSize.Height * padding) / CorrectImageSize.Height;
        Scale = Math.Min(scaleX, scaleY);

        OffsetX = (ViewportSize.Width - (CorrectImageSize.Width * Scale)) / 2;
        OffsetY = (ViewportSize.Height - (CorrectImageSize.Height * Scale)) / 2;
        
        UpdateTargetMarkerPosition();
    }

    // --- Comandi (Soglie) ---

    [RelayCommand(CanExecute = nameof(CanResetThresholds))]
    private async Task ResetThresholds()
    {
        await ApplyOptimalStretchAsync();
    }
    private bool CanResetThresholds() => ActiveImage != null;

    /// <summary>
    /// Metodo helper che esegue lo stretch automatico sull'immagine
    /// e aggiorna i valori Black/WhitePoint del ViewModel.
    /// </summary>
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
        // Aggiunta guardia ridondante per sicurezza
        if (!CanShowPrevious()) return; 
        
        await LoadStackImageAtIndexAsync(_currentStackIndex - 1);
    }
    private bool CanShowPrevious() => IsStack && _currentStackIndex > 0;

    [RelayCommand(CanExecute = nameof(CanShowNext))]
    private async Task NextImage()
    {
        // Aggiunta guardia ridondante per sicurezza
        if (!CanShowNext()) return; 
        
        await LoadStackImageAtIndexAsync(_currentStackIndex + 1);
    }
    private bool CanShowNext() => IsStack && _currentStackIndex < _totalStackCount - 1;


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
        TargetCoordinate = imageCoordinate; // Imposta la proprietà
        
        // Salva la coordinata per l'indice corrente se siamo in uno stack
        if (IsStack)
        {
            _stackCoordinates[_currentStackIndex] = imageCoordinate;
        }
        
        Debug.WriteLine($"[AlgnVM] Clic Preciso! Coordinata Immagine: {imageCoordinate}");
    }
 
    public void ClearTarget()
    {
        TargetCoordinate = null; // Imposta la proprietà

        // Salva "null" per l'indice corrente se siamo in uno stack
        if (IsStack)
        {
            _stackCoordinates[_currentStackIndex] = null;
        }
        
        Debug.WriteLine("[AlgnVM] Mirino rimosso.");
    }
    
    public void UpdateTargetMarkerPosition()
    {
        // Notifica il cambio, il calcolo è nelle proprietà X/Y
        OnPropertyChanged(nameof(TargetMarkerScreenX));
        OnPropertyChanged(nameof(TargetMarkerScreenY));
    }
}