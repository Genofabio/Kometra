using System;
using System.Threading.Tasks;
using Avalonia;
using KomaLab.Models;
using KomaLab.Services;
using nom.tam.fits;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.Generic;
using System.Linq;

namespace KomaLab.ViewModels;

/// <summary>
/// ViewModel per un nodo che visualizza una singola immagine FITS.
/// </summary>
public partial class SingleImageNodeViewModel : BaseNodeViewModel 
{
    // --- Campi ---
    private readonly IFitsService _fitsService;
    private readonly IImageProcessingService _processingService; 
    private readonly SingleImageNodeModel _imageModel;
    
    /// <summary>
    /// Questo è lo "stato attuale" dei dati (originali o processati).
    /// </summary>
    private FitsImageData? _currentData;

    [ObservableProperty]
    private Helpers.FitsDisplayViewModel _fitsImage;
    
    public string ImagePath => _imageModel.ImagePath;

    // --- Implementazione Proprietà Astratte ---
    protected override Size NodeContentSize
    {
        get
        {
            // Legge la dimensione dal "motore" immagine
            if (FitsImage.ImageSize == default(Size))
                return new Size(200, 150); // Dimensione di fallback
            
            return FitsImage.ImageSize;
        }
    }

    // --- Costruttore ---
    public SingleImageNodeViewModel(
        BoardViewModel parentBoard, 
        SingleImageNodeModel model,
        IFitsService fitsService,
        IImageProcessingService processingService) 
        : base(parentBoard, model)
    {
        _fitsService = fitsService;
        _processingService = processingService;
        _imageModel = model;
        
        // Crea un "motore" segnaposto vuoto
        var placeholderModel = new FitsImageData
        {
            RawData = Array.Empty<byte[]>(),
            FitsHeader = new Header(),
            ImageSize = default
        };
        
        _fitsImage = new Helpers.FitsDisplayViewModel(
            placeholderModel, 
            _fitsService, 
            _processingService);
    }
    
    // --- Metodi ---
    
    partial void OnFitsImageChanged(Helpers.FitsDisplayViewModel? value)
    {
        // 1. Notifico che la dimensione del nodo è cambiata
        OnPropertyChanged(nameof(NodeContentSize));
        
        // 2. Avvia l'inizializzazione asincrona (che calcola le soglie)
        _ = value?.InitializeAsync();
    }

    /// <summary>
    /// Carica i dati FITS *originali* dal disco.
    /// </summary>
    public async Task LoadDataAsync()
    {
        try
        {
            var imageData = await _fitsService.LoadFitsFromFileAsync(_imageModel.ImagePath); 
            if (imageData == null)
            {
                throw new Exception("I dati FITS caricati sono nulli o non validi.");
            }
            
            // Applica questi dati come "stato attuale"
            await ApplyProcessedDataAsync(new List<FitsImageData> { imageData });
        }
        catch (Exception ex)
        {
            Title = $"ERRE: {ex.Message.Split('\n')[0]}";
        }
    }
    
    /// <summary>
    /// Sostituisce l'immagine visualizzata con i nuovi dati.
    /// </summary>
    private async Task SetFitsData(FitsImageData newData)
    {
        _currentData = newData; // Salva il new stato
        
        FitsImage.UnloadData();
        FitsImage = new Helpers.FitsDisplayViewModel(
            newData, 
            _fitsService, 
            _processingService);
            
        // InitializeAsync calcolerà le soglie corrette
        // (ignorando 0.0 se è un'immagine processata)
        await FitsImage.InitializeAsync();
    }
    
    // --- Implementazione Metodi Base ---
    
    public override async Task<List<FitsImageData?>> GetCurrentDataAsync()
    {
        // Se i dati non sono ancora stati caricati, caricali ora
        if (_currentData == null)
        {
            await LoadDataAsync();
        }
        return new List<FitsImageData?> { _currentData };
    }

    public override async Task ApplyProcessedDataAsync(List<FitsImageData> newProcessedData)
    {
        if (newProcessedData.Count == 0)
            return;

        var newData = newProcessedData.FirstOrDefault(); // Prende solo il primo
        if (newData == null)
            return;
            
        await SetFitsData(newData);
    }
}