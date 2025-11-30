using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using KomaLab.Models;
using KomaLab.Services;
using KomaLab.ViewModels.Helpers;
using nom.tam.fits;

namespace KomaLab.ViewModels;

/// <summary>
/// ViewModel per un nodo che visualizza una singola immagine FITS.
/// Eredita da ImageNodeViewModel per sfruttare la logica comune delle immagini.
/// </summary>
public partial class SingleImageNodeViewModel : ImageNodeViewModel
{
    // --- Servizi ---
    private readonly IFitsService _fitsService;
    private readonly IFitsDataConverter _converter;
    private readonly IImageAnalysisService _analysis;
    
    // --- Model Sottostante ---
    private readonly SingleImageNodeModel _imageModel;

    // --- Stato Interno ---
    private FitsImageData? _currentData;

    // --- Proprietà Observable ---
    [ObservableProperty]
    private FitsRenderer _fitsImage;

    // --- Implementazione Proprietà Astratte (da ImageNodeViewModel) ---
    protected override Size NodeContentSize
    {
        get
        {
            // Protezione contro null o dati vuoti
            if (FitsImage.Data.Width == 0)
            {
                return new Size(200, 150); // Fallback size
            }
            
            return new Size(FitsImage.Data.Width, FitsImage.Data.Height);
        }
    }

    // --- Costruttore ---
    public SingleImageNodeViewModel(
        SingleImageNodeModel model,
        IFitsService fitsService,
        IFitsDataConverter converter,      // <--- NUOVO
        IImageAnalysisService analysis)    // <--- NUOVO
        : base(model) 
    {
        _fitsService = fitsService;
        _converter = converter;
        _analysis = analysis;
        _imageModel = model;

        // Inizializzazione placeholder
        var placeholderData = new FitsImageData
        {
            RawData = new Array[0], // Inizializza con array vuoto corretto per il Converter
            FitsHeader = new Header(),
            Width = 0,
            Height = 0
        };

        // Passiamo i nuovi servizi al renderer placeholder
        _fitsImage = new FitsRenderer(
            placeholderData, 
            _fitsService, 
            _converter, 
            _analysis);
    }

    // --- Callback Generati dal Toolkit ---
    partial void OnFitsImageChanged(FitsRenderer? value)
    {
        OnPropertyChanged(nameof(NodeContentSize));
        OnPropertyChanged(nameof(EstimatedTotalSize)); 
    }

    // --- Logica di Caricamento ---
    
    public async Task LoadDataAsync()
    {
        try
        {
            var imageData = await _fitsService.LoadFitsFromFileAsync(_imageModel.ImagePath);
            if (imageData == null)
            {
                throw new Exception("I dati FITS caricati sono nulli.");
            }

            await ApplyProcessedDataAsync(new List<FitsImageData> { imageData });
        }
        catch (Exception ex)
        {
            Title = $"ERR: {ex.Message}";
        }
    }

    private async Task SetFitsData(FitsImageData newData)
    {
        _currentData = newData;

        // 1. Crea il nuovo Helper di rendering con i servizi aggiornati
        var newFitsImage = new FitsRenderer(
            newData,
            _fitsService,
            _converter,
            _analysis);
        
        // Init asincrono (conversione Raw -> Mat)
        await newFitsImage.InitializeAsync();

        // 2. Scambio atomico
        var oldFitsImage = FitsImage;
        FitsImage = newFitsImage;

        // 3. Cleanup
        oldFitsImage.UnloadData();
    }

    // --- Implementazione Metodi Astratti ---

    public override async Task<List<FitsImageData?>> GetCurrentDataAsync()
    {
        if (_currentData == null)
        {
            await LoadDataAsync();
        }
        return [_currentData];
    }

    public override async Task ApplyProcessedDataAsync(List<FitsImageData> newProcessedData)
    {
        if (newProcessedData.Count == 0) return;

        var newData = newProcessedData.FirstOrDefault();
        if (newData != null)
        {
            await SetFitsData(newData);
        }
    }

    public override async Task ResetThresholdsAsync()
    {
        await FitsImage.ResetThresholdsAsync();
    }

    public override FitsImageData? GetActiveImageData()
    {
        return _currentData;
    }
}