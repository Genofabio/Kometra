using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KomaLab.Models;
using KomaLab.Services;

namespace KomaLab.ViewModels;

public partial class MultipleImagesNodeViewModel : BaseNodeViewModel
{
    private const double ESTIMATED_UI_HEIGHT = 60.0;
    
    // --- Campi ---
    private readonly IFitsService _fitsService;
    private readonly List<string> _imagePaths;
    private readonly Size _maxImageSize;
    private readonly int _imageCount;

    // --- Proprietà per la Pila ---
    
    [ObservableProperty]
    private FitsDisplayViewModel? _activeFitsImage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentImageText))]
    [NotifyPropertyChangedFor(nameof(CanShowPrevious))]
    [NotifyPropertyChangedFor(nameof(CanShowNext))]
    private int _currentIndex;
    
    [ObservableProperty]
    private double _blackPoint;

    [ObservableProperty]
    private double _whitePoint;

    public string CurrentImageText => $"{CurrentIndex + 1} / {_imagePaths.Count}";
    public Size MaxImageSize => _maxImageSize;
    public bool CanShowPrevious => CurrentIndex > 0;
    public bool CanShowNext => CurrentIndex < _imageCount - 1;

    // --- Costruttore ---
    
    public MultipleImagesNodeViewModel(
        BoardViewModel parentBoard, 
        NodeModel model, 
        List<string> imagePaths, 
        IFitsService fitsService,
        Size maxSize,
        FitsImageData initialData) // <-- Aggiungi il nuovo parametro
        : base(parentBoard, model)
    {
        _fitsService = fitsService;
        _imagePaths = imagePaths;
        _maxImageSize = maxSize;
        _imageCount = imagePaths.Count; 
        
        // --- 3. CAMBIA LA LOGICA DI INIZIALIZZAZIONE ---
        _currentIndex = 0; // Inizia da 0 perché l'immagine è già caricata

        // Usa i dati pre-caricati per creare la prima immagine
        ActiveFitsImage = new FitsDisplayViewModel(initialData, _fitsService);
        ActiveFitsImage.Initialize();

        // Estrai le soglie dalla prima immagine
        BlackPoint = ActiveFitsImage.BlackPoint;
        WhitePoint = ActiveFitsImage.WhitePoint;
    }

    // --- Comandi di Navigazione ---

    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    private async Task PreviousImage()
    {
        // 1. Aggiungi la logica di selezione QUI
        if (!IsSelected)
        {
            ParentBoard.SetSelectedNode(this);
        }
        
        // 2. Chiama il metodo di caricamento
        await LoadImageAtIndexAsync(CurrentIndex - 1);
    }
    private bool CanGoPrevious() => CurrentIndex > 0;

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private async Task NextImage()
    {
        // 1. Aggiungi la logica di selezione QUI
        if (!IsSelected)
        {
            ParentBoard.SetSelectedNode(this);
        }
        
        // 2. Chiama il metodo di caricamento
        await LoadImageAtIndexAsync(CurrentIndex + 1);
    }
    private bool CanGoNext() => CurrentIndex < _imagePaths.Count - 1;

    // --- Logica di Caricamento Lazy ---

    private async Task LoadImageAtIndexAsync(int index)
    {
        if (index < 0 || index >= _imagePaths.Count || index == CurrentIndex)
            return;

        // 1. Scarica l'immagine vecchia (se esiste) per liberare RAM
        ActiveFitsImage?.UnloadData();

        // 2. Imposta il nuovo indice
        CurrentIndex = index;

        // 3. Carica il *nuovo* modello di dati FITS
        FitsImageData? newModel;
        try
        {
            newModel = await _fitsService.LoadFitsFromFileAsync(_imagePaths[index]);
        }
        catch (System.Exception ex)
        {
            Title = $"Errore: {ex.Message}";
            return; 
        }

        // --- CORREZIONE DI COMPILAZIONE ---
        if (newModel == null)
        {
            Title = "Errore: Dati FITS non validi";
            return; 
        }
    
        ActiveFitsImage = new FitsDisplayViewModel(newModel, _fitsService);
    
        BlackPoint = ActiveFitsImage.BlackPoint;
        WhitePoint = ActiveFitsImage.WhitePoint;
    
        // --- FINE CORREZIONE ---

        ActiveFitsImage.Initialize();

        PreviousImageCommand.NotifyCanExecuteChanged();
        NextImageCommand.NotifyCanExecuteChanged();
    }
    
    partial void OnBlackPointChanged(double value)
    {
        if (ActiveFitsImage != null)
            ActiveFitsImage.BlackPoint = value;
    }
    
    partial void OnWhitePointChanged(double value)
    {
        if (ActiveFitsImage != null)
            ActiveFitsImage.WhitePoint = value;
    }
    
    public Size EstimatedTotalSize => new Size(
        _maxImageSize.Width,
        _maxImageSize.Height + ESTIMATED_UI_HEIGHT);

}