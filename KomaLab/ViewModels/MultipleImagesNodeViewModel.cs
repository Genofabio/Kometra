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
    private Size _maxImageSize;
    private readonly int _imageCount;
    private readonly IImageProcessingService _processingService;
    
    /// <summary>
    /// Cache dei dati in memoria (originali o processati).
    /// </summary>
    private List<FitsImageData?> _processedDataCache;

    // --- NUOVI CAMPI PER SIGMA LOCKING ---
    private bool _hasLockedThresholds = false;
    private double _lockedKBlack; 
    private double _lockedKWhite; 
    // -------------------------------------

    // --- Proprietà (Stato) ---
    
    [ObservableProperty]
    private FitsRenderer? _activeFitsImage;
    
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
        var initialData = _processedDataCache[0];
        if (initialData == null)
        {
            Debug.WriteLine("Errore: i dati iniziali erano nulli in InitializeAsync.");
            return;
        }

        ActiveFitsImage = new FitsRenderer(
            initialData, 
            _fitsService, 
            _processingService);
        
        // 1. Inizializza (Auto-Stretch interno)
        await ActiveFitsImage.InitializeAsync();
        
        // 2. Calcola statistiche per il primo Sigma Locking
        var (mean, sigma) = ActiveFitsImage.GetImageStatistics();
        
        // Calcola i fattori K iniziali basati sull'auto-stretch della prima immagine
        _lockedKBlack = (ActiveFitsImage.BlackPoint - mean) / sigma;
        _lockedKWhite = (ActiveFitsImage.WhitePoint - mean) / sigma;
        _hasLockedThresholds = true;

        // 3. Sincronizza UI
        BlackPoint = ActiveFitsImage.BlackPoint;
        WhitePoint = ActiveFitsImage.WhitePoint;
    }

    // --- Comandi di Navigazione ---

    [RelayCommand(CanExecute = nameof(CanShowPrevious))]
    private async Task PreviousImage()
    {
        if (!IsSelected) ParentBoard.SetSelectedNode(this);
        await LoadImageAtIndexAsync(CurrentIndex - 1);
    }
    
    [RelayCommand(CanExecute = nameof(CanShowNext))]
    private async Task NextImage()
    {
        if (!IsSelected) ParentBoard.SetSelectedNode(this);
        await LoadImageAtIndexAsync(CurrentIndex + 1);
    }
    
    [RelayCommand(CanExecute = nameof(CanResetThresholds))]
    private async Task ResetThresholds()
    {
        if (ActiveFitsImage == null) return;

        // 1. Forza ricalcolo auto-stretch su immagine corrente
        await ActiveFitsImage.ResetThresholdsAsync();

        // 2. Aggiorna UI
        BlackPoint = ActiveFitsImage.BlackPoint;
        WhitePoint = ActiveFitsImage.WhitePoint;
        
        // 3. Ricalcola la ricetta K (Sigma Lock) per le prossime immagini
        UpdateLockedSigmaFactors();
    }
    
    private bool CanResetThresholds() => ActiveFitsImage != null;

    // --- Implementazione Metodi Base (Contratto) ---
    
    public override async Task<List<FitsImageData?>> GetCurrentDataAsync()
    {
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
                }
            }
        }
        return _processedDataCache;
    }

    public override async Task ApplyProcessedDataAsync(List<FitsImageData> newProcessedData)
    {
        _processedDataCache = newProcessedData.Cast<FitsImageData?>().ToList();
    
        if (newProcessedData.Count > 0)
        {
            var newSize = newProcessedData[0].ImageSize;
            if (newSize.Width > 0 && newSize.Height > 0)
            {
                _maxImageSize = newSize;
                OnPropertyChanged(nameof(MaxImageSize));
                OnPropertyChanged(nameof(NodeContentSize));
            }
        }

        int tempIndex = CurrentIndex;
        CurrentIndex = -1; 
        await LoadImageAtIndexAsync(tempIndex);
    }
    
    public override async Task ResetThresholdsAsync()
    {
        // Chiamata esterna per reset (es. dal menu contestuale)
        await ResetThresholds();
    }
    
    public override FitsImageData? GetActiveImageData()
    {
        if (CurrentIndex >= 0 && CurrentIndex < _processedDataCache.Count)
        {
            return _processedDataCache[CurrentIndex];
        }
        return null;
    }
    
    // --- Logica di Caricamento (Usa la Cache) ---

    private async Task LoadImageAtIndexAsync(int index)
    {
        if (index < 0 || index >= _imageCount || index == CurrentIndex)
            return;
    
        // Nota: non scarichiamo subito ActiveFitsImage, lo teniamo vivo finché il nuovo non è pronto
        FitsImageData? dataToShow = await GetOrLoadDataAtIndex(index);
    
        if (dataToShow == null) 
        {
            Debug.WriteLine($"Impossibile mostrare l'immagine {index}, dati nulli.");
            return; 
        }

        // 1. Crea il nuovo renderer
        var newFitsImage = new FitsRenderer(
            dataToShow, 
            _fitsService, 
            _processingService);
        
        // 2. Inizializza (calcola statistiche interne e auto-stretch)
        await newFitsImage.InitializeAsync(); 

        // 3. APPLICA LA LOGICA SIGMA LOCKING
        var (mean, sigma) = newFitsImage.GetImageStatistics();

        if (!_hasLockedThresholds)
        {
            // Caso raro (se InitializeAsync non fosse stato chiamato prima), calcoliamo il Master Lock
            _lockedKBlack = (newFitsImage.BlackPoint - mean) / sigma;
            _lockedKWhite = (newFitsImage.WhitePoint - mean) / sigma;
            _hasLockedThresholds = true;
        }
        else
        {
            // Applichiamo la ricetta Master alla nuova immagine
            double adaptedBlack = mean + (_lockedKBlack * sigma);
            double adaptedWhite = mean + (_lockedKWhite * sigma);
            
            newFitsImage.BlackPoint = adaptedBlack;
            newFitsImage.WhitePoint = adaptedWhite;
        }

        // 4. Swap dei renderer
        CurrentIndex = index;
        var oldFitsImage = ActiveFitsImage;
        ActiveFitsImage = newFitsImage;
        oldFitsImage?.UnloadData();

        // 5. Aggiorna UI (questo farà scattare OnBlackPointChanged, che riconfermerà il lock, ed è ok)
        BlackPoint = newFitsImage.BlackPoint; 
        WhitePoint = newFitsImage.WhitePoint;
    
        PreviousImageCommand.NotifyCanExecuteChanged();
        NextImageCommand.NotifyCanExecuteChanged();
    }
    
    /// <summary>
    /// Helper per la cache: recupera i dati dalla cache o li carica dal disco.
    /// </summary>
    private async Task<FitsImageData?> GetOrLoadDataAtIndex(int index)
    {
        if (index < 0 || index >= _imageCount)
            return null;

        FitsImageData? data = _processedDataCache[index];
        if (data == null)
        {
            try
            {
                data = await _fitsService.LoadFitsFromFileAsync(_multiModel.ImagePaths[index]);
                if (data != null)
                {
                    _processedDataCache[index] = data; 
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Caricamento fallito per {index}: {ex.Message}");
            }
        }
        return data;
    }
    
    // --- Metodi Parziali (Collegati alle proprietà) ---
    
    partial void OnBlackPointChanged(double value)
    {
        if (ActiveFitsImage != null)
        {
            ActiveFitsImage.BlackPoint = value;
            // IMPORTANTE: Se l'utente muove lo slider, aggiorna la ricetta Master
            UpdateLockedSigmaFactors();
        }
    }
    
    partial void OnWhitePointChanged(double value)
    {
        if (ActiveFitsImage != null)
        {
            ActiveFitsImage.WhitePoint = value;
            // IMPORTANTE: Se l'utente muove lo slider, aggiorna la ricetta Master
            UpdateLockedSigmaFactors();
        }
    }

    /// <summary>
    /// Helper fondamentale: aggiorna i fattori K in base alle modifiche manuali dell'utente.
    /// </summary>
    private void UpdateLockedSigmaFactors()
    {
        if (ActiveFitsImage == null) return;

        var (mean, sigma) = ActiveFitsImage.GetImageStatistics();
        
        _lockedKBlack = (BlackPoint - mean) / sigma;
        _lockedKWhite = (WhitePoint - mean) / sigma;
        
        _hasLockedThresholds = true;
    }
}