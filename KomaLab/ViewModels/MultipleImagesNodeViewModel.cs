using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KomaLab.Models;
using KomaLab.Services;
using KomaLab.ViewModels.Helpers; 
using Size = Avalonia.Size;

namespace KomaLab.ViewModels;

public partial class MultipleImagesNodeViewModel : BaseNodeViewModel
{
    // --- Campi ---
    private readonly IFitsService _fitsService;
    private readonly MultipleImagesNodeModel _multiModel;
    private readonly Size _maxImageSize;
    private readonly int _imageCount;
    private readonly IImageProcessingService _processingService;
    
    /// <summary>
    /// Cache dei dati in memoria (originali o processati).
    /// </summary>
    private List<FitsImageData?> _processedDataCache;

    // --- Proprietà (Stato) ---
    
    [ObservableProperty]
    private FitsDisplayViewModel? _activeFitsImage;
    
    public List<string> ImagePaths => _multiModel.ImagePaths;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentImageText))]
    [NotifyPropertyChangedFor(nameof(CanShowPrevious))]
    [NotifyPropertyChangedFor(nameof(CanShowNext))]
    private int _currentIndex;
    
    [ObservableProperty]
    private double _blackPoint;

    [ObservableProperty]
    private double _whitePoint;

    // --- Proprietà Calcolate ---
    public string CurrentImageText => $"{CurrentIndex + 1} / {_imageCount}";
    public Size MaxImageSize => _maxImageSize;
    protected override Size NodeContentSize => MaxImageSize;
    public bool CanShowPrevious => CurrentIndex > 0;
    public bool CanShowNext => CurrentIndex < _imageCount - 1;

    // --- Costruttore ---
    
    public MultipleImagesNodeViewModel(
        BoardViewModel parentBoard, 
        MultipleImagesNodeModel model,
        IFitsService fitsService,
        IImageProcessingService processingService,
        Size maxSize,
        FitsImageData initialData) 
        : base(parentBoard, model)
    {
        _fitsService = fitsService;
        _processingService = processingService;
        _multiModel = model;
        _maxImageSize = maxSize;
        _imageCount = model.ImagePaths.Count; 
        
        _currentIndex = 0;
        
        // Inizializza la cache con 'null' e inserisce la prima immagine
        _processedDataCache = Enumerable.Repeat<FitsImageData?>(null, _imageCount).ToList();
        _processedDataCache[0] = initialData;
    }

    /// <summary>
    /// Metodo di inizializzazione asincrona, chiamato dalla Factory.
    /// </summary>
    public async Task InitializeAsync()
    {
        // Usa i dati iniziali già presenti nella cache
        var initialData = _processedDataCache[0];
        if (initialData == null) // Fallback di sicurezza
        {
            Debug.WriteLine("Errore: i dati iniziali erano nulli in InitializeAsync.");
            return;
        }

        ActiveFitsImage = new FitsDisplayViewModel(
            initialData, 
            _fitsService, 
            _processingService);
        await ActiveFitsImage.InitializeAsync();
        
        BlackPoint = ActiveFitsImage.BlackPoint;
        WhitePoint = ActiveFitsImage.WhitePoint;
    }

    // --- Comandi di Navigazione ---

    [RelayCommand(CanExecute = nameof(CanShowPrevious))]
    private async Task PreviousImage()
    {
        if (!IsSelected)
        {
            ParentBoard.SetSelectedNode(this);
        }
        await LoadImageAtIndexAsync(CurrentIndex - 1);
    }
    
    [RelayCommand(CanExecute = nameof(CanShowNext))]
    private async Task NextImage()
    {
        if (!IsSelected)
        {
            ParentBoard.SetSelectedNode(this);
        }
        await LoadImageAtIndexAsync(CurrentIndex + 1);
    }
    
    [RelayCommand(CanExecute = nameof(CanResetThresholds))]
    private async Task ResetThresholds()
    {
        if (ActiveFitsImage == null) return;
    
        // Delega il reset al "motore", che
        // ricalcolerà sui dati che possiede (dataToShow)
        var (newBlack, newWhite) = await ActiveFitsImage.ResetThresholdsAsync();
    
        // Sincronizza gli slider
        BlackPoint = newBlack;
        WhitePoint = newWhite;
    }
    private bool CanResetThresholds() => ActiveFitsImage != null;

    // --- Implementazione Metodi Base (Contratto) ---
    
    public override async Task<List<FitsImageData?>> GetCurrentDataAsync()
    {
        // 1. Dobbiamo assicurarci che tutti i dati siano caricati
        //    (La finestra di allineamento ha bisogno di TUTTE le immagini)
        for (int i = 0; i < _imageCount; i++)
        {
            if (_processedDataCache[i] == null)
            {
                try
                {
                    _processedDataCache[i] = await _fitsService.LoadFitsFromFileAsync(_multiModel.ImagePaths[i]);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Caricamento fallito per {i}: {ex.Message}");
                    // Lascia null se fallisce
                }
            }
        }
        // 2. Restituisci la cache completa
        return _processedDataCache;
    }

    public override async Task ApplyProcessedDataAsync(List<FitsImageData> newProcessedData)
    {
        // Sovrascrive la cache con i new dati processati
        _processedDataCache = newProcessedData.Cast<FitsImageData?>().ToList();
        
        // Forza il ricaricamento dell'immagine corrente per mostrare la modifica
        int tempIndex = CurrentIndex;
        CurrentIndex = -1; // Trucco per forzare il refresh
        await LoadImageAtIndexAsync(tempIndex);
    }
    
    // --- Logica di Caricamento (Usa la Cache) ---

    private async Task LoadImageAtIndexAsync(int index)
    {
        if (index < 0 || index >= _imageCount || index == CurrentIndex)
            return;
        
        ActiveFitsImage?.UnloadData();
        CurrentIndex = index;

        // --- INIZIO LOGICA CACHE ---
        FitsImageData? dataToShow = _processedDataCache[index];

        // Se non è in cache, caricala dal disco
        if (dataToShow == null)
        {
            try
            {
                dataToShow = await _fitsService.LoadFitsFromFileAsync(_multiModel.ImagePaths[index]);
                if (dataToShow != null)
                {
                    _processedDataCache[index] = dataToShow; // Salva in cache
                }
            }
            catch (Exception ex) 
            { 
                Debug.WriteLine($"Errore caricamento {index}: {ex.Message}"); 
                return; 
            }
        }
        // --- FINE LOGICA CACHE ---
        
        if (dataToShow == null) 
        {
            Debug.WriteLine($"Impossibile mostrare l'immagine {index}, dati nulli.");
            return; 
        }
    
        ActiveFitsImage = new FitsDisplayViewModel(
            dataToShow, 
            _fitsService, 
            _processingService);
        await ActiveFitsImage.InitializeAsync(); 

        BlackPoint = ActiveFitsImage.BlackPoint; 
        WhitePoint = ActiveFitsImage.WhitePoint;

        PreviousImageCommand.NotifyCanExecuteChanged();
        NextImageCommand.NotifyCanExecuteChanged();
    }
    
    // --- Metodi Parziali (Collegati alle proprietà) ---
    
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
}