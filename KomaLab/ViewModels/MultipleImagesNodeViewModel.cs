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
        // Usa i dati iniziali già presenti nella cache
        var initialData = _processedDataCache[0];
        if (initialData == null) // Fallback di sicurezza
        {
            Debug.WriteLine("Errore: i dati iniziali erano nulli in InitializeAsync.");
            return;
        }

        ActiveFitsImage = new FitsRenderer(
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

        // 1. Dì al motore di resettarsi
        await ActiveFitsImage.ResetThresholdsAsync();

        // 2. Leggi i nuovi valori (come nell'altro file)
        BlackPoint = ActiveFitsImage.BlackPoint;
        WhitePoint = ActiveFitsImage.WhitePoint;
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
        // 1. Aggiorna la cache dei dati
        _processedDataCache = newProcessedData.Cast<FitsImageData?>().ToList();
    
        // 2. AGGIORNAMENTO DIMENSIONI AUTOMATICO
        // Se abbiamo ricevuto dei dati validi, aggiorniamo _maxImageSize
        if (newProcessedData.Count > 0)
        {
            // Prendiamo la dimensione della prima immagine.
            // (Grazie al lavoro fatto prima, sappiamo che sono tutte uguali e centrate).
            var newSize = newProcessedData[0].ImageSize;
        
            // Controllo di sicurezza per non impostare dimensioni 0x0
            if (newSize.Width > 0 && newSize.Height > 0)
            {
                _maxImageSize = newSize;
            
                // FONDAMENTALE: Notifichiamo alla UI che le proprietà sono cambiate
                // così Avalonia ridimensionerà il rettangolo del nodo.
                OnPropertyChanged(nameof(MaxImageSize));
                OnPropertyChanged(nameof(NodeContentSize));
            }
        }

        // 3. Forza il ricaricamento dell'immagine corrente per mostrare visivamente la modifica
        int tempIndex = CurrentIndex;
        CurrentIndex = -1; // Trucco per forzare il refresh nel metodo LoadImageAtIndexAsync
        await LoadImageAtIndexAsync(tempIndex);
    }
    
    // --- Logica di Caricamento (Usa la Cache) ---

    private async Task LoadImageAtIndexAsync(int index)
    {
        if (index < 0 || index >= _imageCount || index == CurrentIndex)
            return;
    
        ActiveFitsImage?.UnloadData();
        CurrentIndex = index;

        // --- Assicurati che questa chiamata sia corretta ---
        FitsImageData? dataToShow = await GetOrLoadDataAtIndex(index);
        // --- Fine controllo ---
    
        if (dataToShow == null) 
        {
            Debug.WriteLine($"Impossibile mostrare l'immagine {index}, dati nulli.");
            return; 
        }

        var newFitsImage = new FitsRenderer(
            dataToShow, 
            _fitsService, 
            _processingService);
        await newFitsImage.InitializeAsync(); 

        BlackPoint = newFitsImage.BlackPoint; 
        WhitePoint = newFitsImage.WhitePoint;
    
        var oldFitsImage = ActiveFitsImage;
        ActiveFitsImage = newFitsImage;
        oldFitsImage?.UnloadData();

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
                    _processedDataCache[index] = data; // Salva in cache
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
            ActiveFitsImage.BlackPoint = value;
    }
    
    partial void OnWhitePointChanged(double value)
    {
        if (ActiveFitsImage != null)
            ActiveFitsImage.WhitePoint = value;
    }
}