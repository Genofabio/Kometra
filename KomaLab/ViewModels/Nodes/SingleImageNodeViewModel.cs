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

    private FitsImageData? _currentData;
    private Size _explicitSize; 

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveRenderer))] 
    private FitsRenderer? _fitsImage;

    // --- Implementazione Contratti Base ---
    public override FitsRenderer? ActiveRenderer => FitsImage;
    public override Size NodeContentSize => _explicitSize;
    public string ImagePath => _imageModel.ImagePath;

    public SingleImageNodeViewModel(
        SingleImageNodeModel model,
        IFitsIoService ioService,
        IFitsRendererFactory rendererFactory,
        Size explicitSize,         
        FitsImageData? initialData) 
        : base(model) 
    {
        _imageModel = model ?? throw new ArgumentNullException(nameof(model));
        _ioService = ioService ?? throw new ArgumentNullException(nameof(ioService));
        _rendererFactory = rendererFactory ?? throw new ArgumentNullException(nameof(rendererFactory));
        
        _explicitSize = explicitSize;
        _currentData = initialData;

        // Prepariamo il viewport se abbiamo già i dati
        if (initialData != null)
        {
            Viewport.ImageSize = new Size(initialData.Width, initialData.Height);
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
        if (_currentData == null) await LoadDataAsync();
        if (_currentData == null) return;

        // La classe base gestisce: Init, Contrast Transfer, Swap UI e Cleanup vecchio renderer.
        await ApplyNewRendererAsync(_rendererFactory.Create(_currentData));

        if (centerOnPosition)
        {
            Viewport.ResetView();
        }
    }

    /// <summary>
    /// Applica nuovi dati elaborati (es. post-process) garantendo la continuità visiva.
    /// </summary>
    public override async Task ApplyProcessedDataAsync(List<FitsImageData> newProcessedData)
    {
        if (newProcessedData.Count == 0) return;
        
        _currentData = newProcessedData[0];
        // Passiamo null come profilo per forzare la classe base ad ereditare quello esistente
        await ApplyNewRendererAsync(_rendererFactory.Create(_currentData));
    }

    // --- Gestione I/O ---

    public async Task LoadDataAsync()
    {
        try
        {
            if (string.IsNullOrEmpty(_imageModel.ImagePath)) return;
            var data = await _ioService.LoadAsync(_imageModel.ImagePath);
            if (data != null) _currentData = data;
        }
        catch (Exception ex)
        {
            Title = $"ERR: {Path.GetFileName(_imageModel.ImagePath)}";
            System.Diagnostics.Debug.WriteLine($"[SingleImageNode] Load Error: {ex.Message}");
        }
    }

    public override async Task<List<FitsImageData?>> GetCurrentDataAsync()
    {
        if (_currentData == null) await LoadDataAsync();
        return new List<FitsImageData?> { _currentData };
    }

    public override FitsImageData? GetActiveImageData() => _currentData;

    public override async Task<List<string>> PrepareInputPathsAsync(IFitsIoService ioService)
    {
        if (!string.IsNullOrEmpty(_imageModel.ImagePath) && File.Exists(_imageModel.ImagePath))
            return new List<string> { _imageModel.ImagePath };
        
        // Logica per dati in memoria (salvataggio temporaneo se necessario)
        return new List<string>();
    }

    public override async Task RefreshDataFromDiskAsync()
    {
        _currentData = null;
        await InitializeAsync(centerOnPosition: true);
    }

    // --- Cleanup ---

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            FitsImage?.Dispose();
            _currentData = null;
        }
        base.Dispose(disposing);
    }
}