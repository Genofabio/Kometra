using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using KomaLab.Models;
using KomaLab.Services.Data;
using KomaLab.Services.Imaging;
using KomaLab.ViewModels.Helpers;
using nom.tam.fits;

namespace KomaLab.ViewModels;

/// <summary>
/// ViewModel per un nodo che visualizza una singola immagine FITS.
/// Gestisce il caricamento, il rendering e il mantenimento dello stato di una singola risorsa.
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
    private ContrastProfile? _lastContrastProfile;
    public string ImagePath => _imageModel.ImagePath;

    // --- Proprietà Observable ---
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveRenderer))] // Notifica la base
    private FitsRenderer? _fitsImage;

    // --- Implementazione Base ---
    public override FitsRenderer? ActiveRenderer => FitsImage;

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

        // Inizializzazione con Placeholder (evita null checks ovunque)
        var placeholderData = new FitsImageData
        {
            RawData = Array.Empty<Array>(),
            FitsHeader = new Header(),
            Width = 0,
            Height = 0
        };

        _fitsImage = new FitsRenderer(placeholderData, _fitsService, _converter, _analysis);
    }

    // --- Logica di Caricamento ---
    
    public async Task LoadDataAsync()
    {
        try
        {
            if (string.IsNullOrEmpty(_imageModel.ImagePath)) return;

            // Caricamento IO
            var imageData = await _fitsService.LoadFitsFromFileAsync(_imageModel.ImagePath);
            if (imageData == null)
            {
                throw new Exception("I dati FITS caricati sono nulli.");
            }

            // Applicazione al ViewModel
            await ApplyProcessedDataAsync(new List<FitsImageData> { imageData });
        }
        catch (Exception ex)
        {
            Title = $"ERR: {ex.Message}";
        }
    }

    private async Task SetFitsData(FitsImageData newData)
    {
        // 1. Salvataggio stato precedente (Contrasto)
        // FIX: Catturiamo il profilo SOLO se l'immagine precedente era valida (non placeholder)
        if (FitsImage != null && FitsImage.Data.Width > 0) 
        {
            _lastContrastProfile = FitsImage.CaptureContrastProfile();
        }

        _currentData = newData;

        // 2. Creazione Nuovo Renderer
        var newFitsImage = new FitsRenderer(
            newData,
            _fitsService,
            _converter,
            _analysis);
        
        await newFitsImage.InitializeAsync(); // Qui calcola l'Auto-Stretch corretto

        // 3. Ripristino stato visualizzazione (Contrasto)
        // Se è la prima volta (dal placeholder), _lastContrastProfile sarà null, 
        // quindi SALTIAMO questo blocco e manteniamo l'Auto-Stretch appena calcolato.
        if (_lastContrastProfile != null)
        {
            newFitsImage.ApplyContrastProfile(_lastContrastProfile);
        }

        // 4. Aggiornamento Viewport
        Viewport.ImageSize = newFitsImage.ImageSize;
        
        if (FitsImage == null || FitsImage.Data.Width == 0)
        {
            Viewport.ResetView();
        }

        // 5. Swap Atomico
        var oldFitsImage = FitsImage;
        FitsImage = newFitsImage;

        // 6. Sincronizzazione UI (Slider Base)
        // Ora qui leggerà i valori corretti calcolati da InitializeAsync
        BlackPoint = newFitsImage.BlackPoint;
        WhitePoint = newFitsImage.WhitePoint;

        // 7. Notifiche Layout e Cleanup
        OnPropertyChanged(nameof(NodeContentSize));
        OnPropertyChanged(nameof(EstimatedTotalSize));
        oldFitsImage?.UnloadData();
    }

    // --- Implementazione Metodi Astratti (Dati & IO) ---

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

    // Nota: ResetThresholdsAsync è rimosso perché ereditato dalla base

    public override FitsImageData? GetActiveImageData()
    {
        return _currentData;
    }
    
    public override async Task<List<string>> PrepareInputPathsAsync(IFitsService fitsService)
    {
        // CASO A: File esistente su disco
        if (!string.IsNullOrEmpty(_imageModel.ImagePath) && System.IO.File.Exists(_imageModel.ImagePath))
        {
            return new List<string> { _imageModel.ImagePath };
        }

        // CASO B: Dati in memoria (es. risultato elaborazione) -> Creazione Temp
        if (_currentData != null)
        {
            string cleanTempPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), 
                $"Temp_AlignInput_{Guid.NewGuid()}.fits");

            try 
            {
                await fitsService.SaveFitsFileAsync(_currentData, cleanTempPath);
                return new List<string> { cleanTempPath };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Errore creazione temp: {ex.Message}");
                return new List<string>();
            }
        }

        return new List<string>();
    }
    
    public override async Task RefreshDataFromDiskAsync()
    {
        // Semplicemente ricarichiamo i dati
        await LoadDataAsync(); 
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