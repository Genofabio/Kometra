using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KomaLab.Models;
using KomaLab.Services;
using System.Threading.Tasks;
using System;
using System.Diagnostics; // Per il Debug.WriteLine

namespace KomaLab.ViewModels;

public partial class AlignmentViewModel : ObservableObject
{
    // --- Campi ---
    private readonly IFitsService _fitsService;
    private readonly BaseNodeViewModel _nodeToAlign;
    
    // Dimensione (in pixel) del nostro mirino '+' SULLO SCHERMO
    private const double ReticleSize = 20; 

    // --- Proprietà per la Dimensione della Vista ---
    
    /// <summary>
    /// Contiene la dimensioneattuale del pannello PreviewBorder.
    /// Aggiornato dal code-behind.
    /// </summary>
    public Size ViewportSize { get; set; }

    // --- Proprietà per Pan & Zoom ---
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReticleScreenLeft))]
    [NotifyPropertyChangedFor(nameof(ReticleScreenTop))]
    private double _previewOffsetX;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReticleScreenLeft))]
    [NotifyPropertyChangedFor(nameof(ReticleScreenTop))]
    private double _previewOffsetY;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReticleScreenLeft))]
    [NotifyPropertyChangedFor(nameof(ReticleScreenTop))]
    private double _previewScale = 1.0; 

    // --- Proprietà per l'Immagine ---
    
    // --- MODIFICA CHIAVE ---
    // Rimosso [NotifyPropertyChangedFor(nameof(CorrectImageSize))]
    // per evitare la notifica prematura.
    [ObservableProperty]
    private FitsDisplayViewModel? _activeImage; 
    
    // --- PROPRIETÀ CORRETTA ---
    /// <summary>
    /// Contiene la dimensione reale (es. 1024x1024) dell'immagine bitmap.
    /// </summary>
    public Size CorrectImageSize => ActiveImage?.Image?.Size ?? default;
    
    // --- Proprietà per le Soglie ---
    [ObservableProperty]
    private double _blackPoint;
    [ObservableProperty]
    private double _whitePoint;

    // --- Proprietà per la Selezione (Mirino) ---
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReticleVisible))]
    [NotifyPropertyChangedFor(nameof(ReticleScreenLeft))] // Aggiornato
    [NotifyPropertyChangedFor(nameof(ReticleScreenTop))]  // Aggiornato
    private Point? _targetCoordinate; // Coordinate (X, Y) relative all'IMMAGINE

    // --- Proprietà Calcolate per il XAML ---
    public bool IsReticleVisible => TargetCoordinate.HasValue;
    
    public double ReticleScreenLeft => TargetCoordinate.HasValue
        ? (TargetCoordinate.Value.X * PreviewScale + PreviewOffsetX) - (ReticleSize / 2)
        : 0;
        
    public double ReticleScreenTop => TargetCoordinate.HasValue
        ? (TargetCoordinate.Value.Y * PreviewScale + PreviewOffsetY) - (ReticleSize / 2)
        : 0;
    
    // --- Costruttore ---
    public AlignmentViewModel(BaseNodeViewModel nodeToAlign, IFitsService fitsService)
    {
        _nodeToAlign = nodeToAlign;
        _fitsService = fitsService;
        
        // Carica l'immagine di prova in background
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
            Debug.WriteLine("[AlignmentViewModel] Caricamento immagine di test...");
            
            var imageData = await _fitsService.LoadFitsFromFileAsync(testImagePath);
            
            if (imageData == null)
            {
                Debug.WriteLine("[AlignmentViewModel] ERRORE: imageData è null.");
                return;
            }

            Debug.WriteLine("[AlignmentViewModel] Immagine caricata, creo il motore display...");
            
            // Imposta l'immagine (NON notifica ancora CorrectImageSize)
            ActiveImage = new FitsDisplayViewModel(imageData, _fitsService);
            
            // Inizializza l'immagine (imposta ActiveImage.Image)
            ActiveImage.Initialize();
            
            // ORA notifichiamo la UI che la dimensione è pronta.
            // Questo è il momento sicuro.
            OnPropertyChanged(nameof(CorrectImageSize)); 
            Debug.WriteLine($"[AlignmentViewModel] Dimensione immagine caricata: {CorrectImageSize}");

            Debug.WriteLine("[AlignmentViewModel] Motore creato. Imposto soglie...");
            BlackPoint = ActiveImage.BlackPoint;
            WhitePoint = ActiveImage.WhitePoint;
            
            Debug.WriteLine("[AlignmentViewModel] Soglie impostate. Notifico il comando Reset...");
            ResetThresholdsCommand.NotifyCanExecuteChanged();
            Debug.WriteLine("[AlignmentViewModel] Caricamento completato.");
        }
        catch (System.Exception ex)
        {
            // Questo stamperà l'errore reale nella finestra di Output
            Debug.WriteLine("--- CRASH IN AlignmentViewModel.LoadTestImageAsync ---");
            Debug.WriteLine(ex.ToString());
            Debug.WriteLine("-------------------------------------------------------");
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

    // --- Comandi per i Pulsanti ---

    [RelayCommand]
    private void ZoomIn()
    {
        ApplyZoom(1.2);
    }

    [RelayCommand]
    private void ZoomOut()
    {
        ApplyZoom(1 / 1.2);
    }

    /// <summary>
    /// Applica uno zoom centrato rispetto al ViewportSize.
    /// </summary>
    private void ApplyZoom(double scaleFactor)
    {
        // Se non abbiamo ancora la dimensione, esegui uno zoom "semplice"
        if (ViewportSize.Width == 0 || ViewportSize.Height == 0)
        {
            PreviewScale *= scaleFactor;
            return;
        }

        var viewportCenter = new Point(ViewportSize.Width / 2, ViewportSize.Height / 2);
        
        // 1. Trova quale punto dell'immagine è attualmente al centro
        double imagePointX = (viewportCenter.X - PreviewOffsetX) / PreviewScale;
        double imagePointY = (viewportCenter.Y - PreviewOffsetY) / PreviewScale;

        // 2. Calcola la new scala
        double newScale = PreviewScale * scaleFactor;

        // 3. Calcola il new offset per mantenere quel punto dell'immagine al centro
        double newOffsetX = viewportCenter.X - (imagePointX * newScale);
        double newOffsetY = viewportCenter.Y - (imagePointY * newScale);

        // 4. Applica i new valori
        PreviewScale = newScale;
        PreviewOffsetX = newOffsetX;
        PreviewOffsetY = newOffsetY;
    }

    [RelayCommand]
    private void ResetView()
    {
        PreviewOffsetX = 0;
        PreviewOffsetY = 0;
        PreviewScale = 1.0;
    }

    [RelayCommand(CanExecute = nameof(CanResetThresholds))]
    private async Task ResetThresholds()
    {
        if (ActiveImage == null) return;
        
        // Delega il ricalcolo al "motore" immagine
        var (newBlack, newWhite) = await ActiveImage.ResetThresholdsAsync();
        
        // Aggiorna gli slider
        BlackPoint = newBlack;
        WhitePoint = newWhite;
    }
    
    private bool CanResetThresholds() => ActiveImage != null;

    // --- Metodo Pubblico per il Code-Behind ---
    
    /// <summary>
    /// Chiamato dal code-behind per impostare la coordinata di allineamento.
    /// </summary>
    public void SetTargetCoordinate(Point imageCoordinate)
    {
        TargetCoordinate = imageCoordinate;
        
        // --- RIGA DI DEBUG AGGIUNTA ---
        Debug.WriteLine($"[AlignmentViewModel] Coordinata immagine impostata su: X={imageCoordinate.X}, Y={imageCoordinate.Y}");
        // --- FINE ---
    }
}