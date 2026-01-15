using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using KomaLab.Models.Fits;
using KomaLab.Models.Nodes;
using KomaLab.Services.Factories;
using KomaLab.Services.Fits;
using KomaLab.ViewModels.Visualization;

namespace KomaLab.ViewModels.Nodes;

/// <summary>
/// ViewModel per la gestione di una singola immagine FITS.
/// Sfrutta la logica polimorfica della classe base per garantire inizializzazione asincrona e swap sicuri.
/// </summary>
public partial class SingleImageNodeViewModel : ImageNodeViewModel
{
    private readonly IFitsIoService _ioService;
    private readonly IFitsRendererFactory _rendererFactory;
    private readonly SingleImageNodeModel _imageModel;

    // --- Stato Dati ---
    // Usiamo una collezione di 1 elemento per compatibilità con il resto del sistema
    private FitsCollection _collection; 
    private Size _explicitSize; 

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveRenderer))] 
    private FitsRenderer? _fitsImage;

    // --- Implementazione Contratti Base ---
    public override FitsRenderer? ActiveRenderer => FitsImage;
    public override Size NodeContentSize => _explicitSize;
    public string ImagePath => _imageModel.ImagePath;

    // Nuovi contratti astratti
    public override FitsCollection? OutputCollection => _collection;
    public override FitsFileReference? ActiveFile => _collection.Count > 0 ? _collection[0] : null;

    public SingleImageNodeViewModel(
        SingleImageNodeModel model,
        IFitsIoService ioService,
        IFitsRendererFactory rendererFactory,
        Size explicitSize,         
        FitsCollection? initialCollection = null) // Ora accetta una Collection opzionale
        : base(model) 
    {
        _imageModel = model ?? throw new ArgumentNullException(nameof(model));
        _ioService = ioService ?? throw new ArgumentNullException(nameof(ioService));
        _rendererFactory = rendererFactory ?? throw new ArgumentNullException(nameof(rendererFactory));
        
        _explicitSize = explicitSize;

        // Se non viene passata una collezione (es. nodo sorgente), ne creiamo una dal path del modello
        if (initialCollection != null)
        {
            _collection = initialCollection;
        }
        else if (!string.IsNullOrEmpty(model.ImagePath))
        {
            _collection = new FitsCollection(new[] { model.ImagePath }, cacheSize: 1);
        }
        else
        {
            _collection = new FitsCollection(Array.Empty<string>(), cacheSize: 1);
        }
    }

    /// <summary>
    /// Implementazione obbligatoria per la classe base: aggiorna il riferimento locale del renderer.
    /// Viene chiamata internamente da ApplyNewRendererAsync sul thread UI.
    /// </summary>
    protected override void OnRendererSwapping(FitsRenderer newRenderer)
    {
        FitsImage = newRenderer;
        _explicitSize = newRenderer.ImageSize;
    }

    /// <summary>
    /// Inizializzazione asincrona completa tramite la classe base.
    /// </summary>
    public async Task InitializeAsync(bool centerOnPosition = false)
    {
        if (_collection.Count == 0) return;

        await LoadDataAsync();

        if (centerOnPosition)
        {
            Viewport.ResetView();
        }
    }

    // --- Gestione I/O e Loading ---

    private async Task LoadDataAsync()
    {
        if (_collection.Count == 0) return;
        var fileRef = _collection[0];

        try
        {
            // 1. Recupero Dati (Header + Pixels)
            // Header: Priorità a quello in memoria (se modificato)
            FitsHeader? header = fileRef.HasUnsavedChanges 
                ? fileRef.UnsavedHeader 
                : await _ioService.ReadHeaderAsync(fileRef.FilePath);

            // Pixel: Controllo Cache Collettiva
            Array? pixels;
            if (_collection.PixelCache.TryGet(fileRef.FilePath, out var cachedPixels))
            {
                pixels = cachedPixels;
            }
            else
            {
                pixels = await _ioService.ReadPixelDataAsync(fileRef.FilePath);
                // Popola cache se caricato con successo
                if (pixels != null) _collection.PixelCache.Add(fileRef.FilePath, pixels);
            }

            if (header == null || pixels == null) return;

            // 2. Creazione Renderer
            var newRenderer = _rendererFactory.Create(pixels, header);
            
            // Inizializza statistiche e Matrice interna
            await newRenderer.InitializeAsync();

            // 3. Applicazione tramite Base (gestisce contrasto, swap UI, cleanup)
            await ApplyNewRendererAsync(newRenderer);
        }
        catch (Exception ex)
        {
            Title = $"ERR: {Path.GetFileName(_imageModel.ImagePath)}";
            System.Diagnostics.Debug.WriteLine($"[SingleImageNode] Load Error: {ex.Message}");
        }
    }

    // --- Implementazione Metodi Astratti Nuovi ---

    public override async Task LoadInputAsync(FitsCollection input)
    {
        _collection = input;
        
        // Se riceviamo nuovi dati (es. da un nodo processore), ricarichiamo la visualizzazione
        if (_collection.Count > 0)
        {
            await LoadDataAsync();
        }
        else
        {
            // Gestione caso vuoto (opzionale: pulire il renderer)
        }
    }

    public override async Task RefreshDataFromDiskAsync()
    {
        // Pulisce la cache per questo file specifico
        if (_collection.Count > 0)
        {
            _collection.PixelCache.Remove(_collection[0].FilePath);
        }
        
        await LoadDataAsync();
    }

    // --- Cleanup ---

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            FitsImage?.Dispose();
            // Nota: Non disponiamo la _collection perché potrebbe essere passata avanti
        }
        base.Dispose(disposing);
    }
}