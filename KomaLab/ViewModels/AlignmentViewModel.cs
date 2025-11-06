using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input; // <-- Aggiunto per i comandi
using KomaLab.Models;
using KomaLab.Services;
using System.Diagnostics;
using System.Threading.Tasks;
using System; 

namespace KomaLab.ViewModels;

public partial class AlignmentViewModel : ObservableObject
{
    private readonly IFitsService _fitsService;
    private readonly BaseNodeViewModel _nodeToAlign; // Il nodo REALE
    
    // Dimensione (in pixel) del nostro mirino '+' SULLO SCHERMO
    private const double ReticleSize = 20; 

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
    [NotifyPropertyChangedFor(nameof(ReticleStrokeThickness))] // Aggiunta notifica
    private double _scale = 1.0; 

    // --- Proprietà per l'Immagine ---
    [ObservableProperty]
    private FitsDisplayViewModel? _activeImage; 
    
    public Size CorrectImageSize => ActiveImage?.Image?.Size ?? default;
    
    // --- AGGIUNTA: Proprietà per le Soglie ---
    [ObservableProperty]
    private double _blackPoint;
    [ObservableProperty]
    private double _whitePoint;

    // --- Proprietà per la Selezione (Mirino) ---
    private Point? _targetMarkerPosition;
    public Point? TargetMarkerPosition
    {
        get => _targetMarkerPosition;
        set
        {
            if (SetProperty(ref _targetMarkerPosition, value))
            {
                OnPropertyChanged(nameof(IsTargetMarkerVisible));
                OnPropertyChanged(nameof(TargetMarkerScreenX));
                OnPropertyChanged(nameof(TargetMarkerScreenY));
            }
        }
    }
    public bool IsTargetMarkerVisible => _targetMarkerPosition.HasValue;
    public double TargetMarkerScreenX => _targetMarkerPosition?.X ?? 0;
    public double TargetMarkerScreenY => _targetMarkerPosition?.Y ?? 0;
    
    // AGGIUNTA: Proprietà per lo spessore del mirino
    public double ReticleStrokeThickness => 1.0 / Scale;

    [ObservableProperty]
    private Point? _targetCoordinate; // Il punto cliccato sull'immagine

    // Task per segnalare che l'immagine è caricata
    public TaskCompletionSource ImageLoadedTcs { get; } = new();

    // --- Costruttore (Ora accetta il nodo reale) ---
    public AlignmentViewModel(BaseNodeViewModel nodeToAlign, IFitsService fitsService)
    {
        _nodeToAlign = nodeToAlign;
        _fitsService = fitsService;
        
        // Carica l'immagine (per ora di test, poi la prenderemo dal nodo)
        _ = LoadTestImageAsync();
    }

    /// <summary>
    /// Metodo temporaneo per caricare un'immagine di prova.
    /// </summary>
    private async Task LoadTestImageAsync()
    {
        try
        {
            string testImagePath = "avares://KomaLab/Assets/summed_clean.fits";
            var imageData = await _fitsService.LoadFitsFromFileAsync(testImagePath);
            
            if (imageData == null)
            {
                Debug.WriteLine("[AlgnVM] ERRORE: imageData è null.");
                return;
            }

            ActiveImage = new FitsDisplayViewModel(imageData, _fitsService);
            ActiveImage.Initialize();
            
            OnPropertyChanged(nameof(CorrectImageSize)); 

            // Imposta le soglie
            BlackPoint = ActiveImage.BlackPoint;
            WhitePoint = ActiveImage.WhitePoint;
            
            // Notifica il comando
            ResetThresholdsCommand.NotifyCanExecuteChanged();
        }
        catch (System.Exception ex)
        {
            Debug.WriteLine($"--- CRASH IN AlgnVM.LoadTestImageAsync --- {ex}");
        }
        finally
        {
            ImageLoadedTcs.SetResult();
        }
    }
    
    // --- AGGIUNTA: Metodi Parziali per le Soglie ---
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

    // --- AGGIUNTA: Comandi per i Pulsanti ---
    [RelayCommand] private void ZoomIn() { }
    [RelayCommand] private void ZoomOut() { }
    [RelayCommand] private void ResetView() { }

    [RelayCommand(CanExecute = nameof(CanResetThresholds))]
    private async Task ResetThresholds()
    {
        if (ActiveImage == null) return;
        var (newBlack, newWhite) = await ActiveImage.ResetThresholdsAsync();
        BlackPoint = newBlack;
        WhitePoint = newWhite;
    }
    private bool CanResetThresholds() => ActiveImage != null;

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
        TargetCoordinate = imageCoordinate;
        Debug.WriteLine($"[AlgnVM] Clic Preciso! Coordinata Immagine: {imageCoordinate}");
        UpdateTargetMarkerPosition();
    }

    public void ClearTarget()
    {
        TargetCoordinate = null;
        Debug.WriteLine("[AlgnVM] Mirino rimosso.");
        UpdateTargetMarkerPosition();
    }
    
    public void UpdateTargetMarkerPosition()
    {
        if (TargetCoordinate.HasValue && ActiveImage != null)
        {
            double markerX = OffsetX + (TargetCoordinate.Value.X * Scale);
            double markerY = OffsetY + (TargetCoordinate.Value.Y * Scale);
            TargetMarkerPosition = new Point(markerX, markerY);
        }
        else
        {
            TargetMarkerPosition = null;
        }
    }
}