using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using KomaLab.Models;
using KomaLab.Services;
using KomaLab.Services.Data;
using KomaLab.Services.Imaging;
using KomaLab.ViewModels.Helpers;
using nom.tam.fits;

namespace KomaLab.ViewModels;

/// <summary>
/// ViewModel per un nodo che visualizza una singola immagine FITS.
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
    public ImageViewport Viewport { get; } = new();

    // --- Proprietà Observable ---
    [ObservableProperty]
    private FitsRenderer? _fitsImage;

    // --- Implementazione Proprietà Astratte ---
    protected override Size NodeContentSize
    {
        get
        {
            if (FitsImage?.Data == null || FitsImage.Data.Width == 0)
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
        IFitsDataConverter converter,      
        IImageAnalysisService analysis)    
        : base(model) 
    {
        _fitsService = fitsService;
        _converter = converter;
        _analysis = analysis;
        _imageModel = model;

        // Placeholder vuoto
        var placeholderData = new FitsImageData
        {
            RawData = new Array[0],
            FitsHeader = new Header(),
            Width = 0,
            Height = 0
        };

        _fitsImage = new FitsRenderer(
            placeholderData, 
            _fitsService, 
            _converter, 
            _analysis);
    }

    // --- Callback Generati ---
    partial void OnFitsImageChanged(FitsRenderer? value)
    {
        OnPropertyChanged(nameof(NodeContentSize));
        OnPropertyChanged(nameof(EstimatedTotalSize)); 

        if (value != null)
        {
            // 1. Informiamo il Viewport delle dimensioni reali dell'immagine
            Viewport.ImageSize = value.ImageSize;
            
            // 2. Resettiamo la vista (Zoom to fit)
            // Nota: Viewport.ViewportSize deve essere aggiornato dalla View (vedi punto 3 sotto)
            Viewport.ResetView();
        }
    }

    // --- Logica di Caricamento ---
    
    public async Task LoadDataAsync()
    {
        try
        {
            if (string.IsNullOrEmpty(_imageModel.ImagePath)) return;

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

        // 1. Crea nuovo Renderer
        var newFitsImage = new FitsRenderer(
            newData,
            _fitsService,
            _converter,
            _analysis);
        
        await newFitsImage.InitializeAsync();

        // 2. Swap atomico
        var oldFitsImage = FitsImage;
        FitsImage = newFitsImage;

        // 3. Cleanup immediato
        oldFitsImage?.UnloadData();
    }

    // --- Implementazione Metodi Astratti ---

    public override async Task<List<FitsImageData?>> GetCurrentDataAsync()
    {
        if (_currentData == null)
        {
            await LoadDataAsync();
        }
        return new List<FitsImageData?> { _currentData };
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
        // FIX SICUREZZA: Controllo esplicito prima dell'await
        if (FitsImage != null)
        {
            await FitsImage.ResetThresholdsAsync();
        }
    }

    public override FitsImageData? GetActiveImageData()
    {
        return _currentData;
    }
    
    public override async Task<List<string>> PrepareInputPathsAsync(IFitsService fitsService)
    {
        // CASO A: Il file esiste già su disco (es. caricato dall'utente)
        if (!string.IsNullOrEmpty(_imageModel.ImagePath) && System.IO.File.Exists(_imageModel.ImagePath))
        {
            return new List<string> { _imageModel.ImagePath };
        }

        // CASO B: I dati sono solo in memoria (es. risultato di uno Stack)
        if (_currentData != null)
        {
            // Creiamo un file temporaneo
            string tempPath = System.IO.Path.GetTempFileName(); 
            // Nota: GetTempFileName crea un file 0-byte, a volte le lib FITS si lamentano.
            // Meglio cancellarlo e ricrearlo col nome giusto o usare GUID.
            string cleanTempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"Temp_AlignInput_{Guid.NewGuid()}.fits");

            try 
            {
                await fitsService.SaveFitsFileAsync(_currentData, cleanTempPath);
                return new List<string> { cleanTempPath };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Errore creazione temp per allineamento: {ex.Message}");
                return new List<string>();
            }
        }

        return new List<string>();
    }
    
    // --- Gestione Memoria (IDisposable) ---
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            FitsImage?.UnloadData();
            FitsImage = null;
            _currentData = null;
        }
        
        base.Dispose(disposing);
    }
}