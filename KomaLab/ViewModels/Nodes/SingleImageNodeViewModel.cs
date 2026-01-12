using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using KomaLab.Models.Fits;
using KomaLab.Models.Nodes;
using KomaLab.Models.Visualization;
using KomaLab.Services.Fits;
using KomaLab.Services.Processing;
using KomaLab.ViewModels.Visualization; // Namespace corretto per FitsRenderer

namespace KomaLab.ViewModels.Nodes;

// ---------------------------------------------------------------------------
// FILE: SingleImageNodeViewModel.cs
// RUOLO: ViewModel Nodo Immagine Singola
// DESCRIZIONE:
// Specializzazione di ImageNodeViewModel per gestire un singolo file FITS.
// Orchesta il caricamento I/O e la creazione del FitsRenderer.
// ---------------------------------------------------------------------------

public partial class SingleImageNodeViewModel : ImageNodeViewModel
{
    // --- Dipendenze ---
    private readonly IFitsIoService _ioService;
    private readonly IFitsImageDataConverter _converter;
    private readonly IImageAnalysisService _analysis;
    
    // --- Model ---
    private readonly SingleImageNodeModel _imageModel;

    // --- Stato Interno ---
    // Manteniamo un riferimento ai dati grezzi per operazioni I/O (es. salvataggio temp)
    private FitsImageData? _currentData;
    private ContrastProfile? _lastContrastProfile;
    
    // Shortcut per la View
    public string ImagePath => _imageModel.ImagePath;

    // --- Proprietà Observable ---

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveRenderer))] // Notifica la classe base
    private FitsRenderer? _fitsImage;

    // --- Override ImageNodeViewModel ---

    public override FitsRenderer? ActiveRenderer => FitsImage;

    protected override Size NodeContentSize
    {
        get
        {
            if (FitsImage?.Data == null || FitsImage.Data.Width == 0)
            {
                // Dimensione default se nessuna immagine è caricata
                return new Size(250, 200); 
            }
            return new Size(FitsImage.Data.Width, FitsImage.Data.Height);
        }
    }

    // --- Costruttore ---

    public SingleImageNodeViewModel(
        SingleImageNodeModel model,
        IFitsIoService ioService,
        IFitsImageDataConverter converter,      
        IImageAnalysisService analysis)    
        : base(model) 
    {
        _imageModel = model ?? throw new ArgumentNullException(nameof(model));
        _ioService = ioService ?? throw new ArgumentNullException(nameof(ioService));
        _converter = converter ?? throw new ArgumentNullException(nameof(converter));
        _analysis = analysis ?? throw new ArgumentNullException(nameof(analysis));

        // NOTA: Non creiamo nessun Placeholder qui. 
        // Risparmiamo RAM e CPU lasciando FitsImage a null finché non carichiamo dati veri.
    }

    // --- Logica di Caricamento ---
    
    public async Task LoadDataAsync()
    {
        try
        {
            if (string.IsNullOrEmpty(_imageModel.ImagePath)) return;

            // 1. Caricamento IO (Heavy I/O Bound)
            var imageData = await _ioService.LoadAsync(_imageModel.ImagePath);
            if (imageData == null)
            {
                throw new IOException($"Impossibile caricare i dati FITS da: {_imageModel.ImagePath}");
            }

            // 2. Applicazione al ViewModel (passando per il metodo standard)
            await ApplyProcessedDataAsync(new List<FitsImageData> { imageData });
        }
        catch (Exception ex)
        {
            // Visualizza l'errore nel titolo del nodo per feedback immediato
            Title = $"ERR: {ex.Message}";
            System.Diagnostics.Debug.WriteLine($"[SingleImageNode] Load Error: {ex}");
        }
    }

    /// <summary>
    /// Metodo centrale per l'aggiornamento dell'immagine.
    /// Gestisce la creazione del renderer e la migrazione dello stato (contrasto/zoom).
    /// </summary>
    private async Task SetFitsData(FitsImageData newData)
    {
        // 1. Cattura stato precedente (Contrasto)
        if (FitsImage != null && !_isDisposed) 
        {
            _lastContrastProfile = FitsImage.CaptureContrastProfile();
        }

        _currentData = newData;

        // 2. Creazione Nuovo Renderer (Allocazione RAM/VRAM)
        var newFitsImage = new FitsRenderer(
            newData,
            _ioService,
            _converter,
            _analysis);
        
        // 3. Inizializzazione Pipeline (Calcolo Auto-Stretch)
        await newFitsImage.InitializeAsync(); 

        // 4. Ripristino Configurazione Visuale
        // Applichiamo la modalità (Log/Linear) definita nel ViewModel
        newFitsImage.VisualizationMode = this.VisualizationMode;

        // Applichiamo il vecchio contrasto se esisteva, altrimenti teniamo l'Auto-Stretch calcolato in init
        if (_lastContrastProfile != null)
        {
            newFitsImage.ApplyContrastProfile(_lastContrastProfile);
        }

        // 5. Swap e Cleanup
        var oldFitsImage = FitsImage;
        FitsImage = newFitsImage;

        // Liberiamo SUBITO la memoria vecchia per ridurre il picco (RAM Spike)
        oldFitsImage?.Dispose();

        // 6. Aggiornamento Viewport e UI
        Viewport.ImageSize = newFitsImage.ImageSize;
        
        // Reset vista solo se è il primo caricamento
        if (oldFitsImage == null)
        {
            Viewport.ResetView();
        }

        // Sincronizza gli slider UI con i valori del nuovo renderer
        BlackPoint = newFitsImage.BlackPoint;
        WhitePoint = newFitsImage.WhitePoint;

        // Notifica cambiamento dimensioni al sistema di layout dei nodi
        OnPropertyChanged(nameof(NodeContentSize));
        OnPropertyChanged(nameof(EstimatedTotalSize));
    }

    // --- Implementazione Contratti Astratti ---

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
        if (newProcessedData == null || newProcessedData.Count == 0) return;

        var newData = newProcessedData[0];
        if (newData != null)
        {
            await SetFitsData(newData);
        }
    }

    public override FitsImageData? GetActiveImageData()
    {
        return _currentData;
    }
    
    public override async Task<List<string>> PrepareInputPathsAsync(IFitsIoService ioService)
    {
        // A. File su disco esistente: ritorniamo il path originale
        if (!string.IsNullOrEmpty(_imageModel.ImagePath) && File.Exists(_imageModel.ImagePath))
        {
            return new List<string> { _imageModel.ImagePath };
        }

        // B. Dati in memoria (senza file): creiamo un temporaneo
        if (_currentData != null)
        {
            string tempPath = Path.Combine(Path.GetTempPath(), $"KomaLab_Node_{Guid.NewGuid()}.fits");
            try 
            {
                await ioService.SaveAsync(_currentData, tempPath);
                return new List<string> { tempPath };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SingleImageNode] Temp Save Error: {ex.Message}");
                return new List<string>();
            }
        }

        return new List<string>();
    }
    
    public override async Task RefreshDataFromDiskAsync()
    {
        await LoadDataAsync(); 
    }
    
    // --- Gestione Risorse (IDisposable) ---

    // Flag per evitare chiamate multiple durante il dispose
    private bool _isDisposed;

    protected override void Dispose(bool disposing)
    {
        if (!_isDisposed)
        {
            if (disposing)
            {
                // Dispose del renderer attivo (libera VRAM e Mat OpenCV)
                FitsImage?.Dispose();
                FitsImage = null;
                
                // Dereferencing dei dati raw (libera RAM Managed)
                _currentData = null;
            }
            _isDisposed = true;
        }
        
        base.Dispose(disposing);
    }
}