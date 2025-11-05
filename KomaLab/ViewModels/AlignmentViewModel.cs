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
    
    // Dimensione (in pixel) del nostro mirino '+'
    private const double ReticleSize = 20; 

    // --- Proprietà per Pan & Zoom ---
    [ObservableProperty]
    private double _previewOffsetX;
    [ObservableProperty]
    private double _previewOffsetY;
    [ObservableProperty]
    private double _previewScale = 1.0; 

    // --- Proprietà per l'Immagine ---
    [ObservableProperty]
    private FitsDisplayViewModel? _activeImage; 
    
    // --- Proprietà per le Soglie ---
    [ObservableProperty]
    private double _blackPoint;
    [ObservableProperty]
    private double _whitePoint;

    // --- Proprietà per la Selezione (Mirino) ---
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReticleVisible))]
    [NotifyPropertyChangedFor(nameof(ReticleCanvasLeft))]
    [NotifyPropertyChangedFor(nameof(ReticleCanvasTop))]
    private Point? _targetCoordinate; // Coordinate (X, Y) relative all'IMMAGINE

    // --- Proprietà Calcolate per il XAML ---
    public bool IsReticleVisible => TargetCoordinate.HasValue;
    public double ReticleCanvasLeft => TargetCoordinate?.X - (ReticleSize / 2) ?? 0;
    public double ReticleCanvasTop => TargetCoordinate?.Y - (ReticleSize / 2) ?? 0;
    
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
    /// Contiene codice di debug per trovare l'errore "schermo nero".
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
            ActiveImage = new FitsDisplayViewModel(imageData, _fitsService);
            ActiveImage.Initialize();
            
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
        PreviewScale *= 1.2;
    }

    [RelayCommand]
    private void ZoomOut()
    {
        PreviewScale /= 1.2;
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
    }
}