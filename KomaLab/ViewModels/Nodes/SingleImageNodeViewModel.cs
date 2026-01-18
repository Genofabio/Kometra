using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using KomaLab.Models.Fits;
using KomaLab.Models.Nodes;
using KomaLab.Services.Factories;
using KomaLab.Services.Fits;
using KomaLab.ViewModels.Components;
using KomaLab.ViewModels.Visualization;

namespace KomaLab.ViewModels.Nodes;

/// <summary>
/// Nodo per la gestione di una singola immagine FITS.
/// Implementa IImageNavigator tramite un SequenceNavigator fisso (1/1) 
/// per garantire coerenza totale con i nodi multipli e i tool.
/// </summary>
public partial class SingleImageNodeViewModel : ImageNodeViewModel
{
    private readonly IFitsDataManager _dataManager;
    private readonly IFitsRendererFactory _rendererFactory;
    
    private FitsFileReference _fileReference;
    private CancellationTokenSource? _loadingCts;
    
    // Componente di navigazione coerente (fisso a 1 immagine)
    private readonly SequenceNavigator _navigator = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveRenderer))]
    private FitsRenderer? _activeFitsImage;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _errorMessage;

    // ---------------------------------------------------------------------------
    // OVERRIDES CAPABILITY & LAYOUT
    // ---------------------------------------------------------------------------

    public override IImageNavigator Navigator => _navigator;
    public override FitsRenderer? ActiveRenderer => _activeFitsImage;
    public override Size NodeContentSize => ActiveRenderer?.ImageSize ?? default;
    public override IReadOnlyList<FitsFileReference> CurrentFiles => new[] { _fileReference };
    public override FitsFileReference? ActiveFile => _fileReference;

    // ---------------------------------------------------------------------------
    // COSTRUTTORE E INIZIALIZZAZIONE
    // ---------------------------------------------------------------------------

    public SingleImageNodeViewModel(
        SingleImageNodeModel model, 
        IFitsDataManager dataManager, 
        IFitsRendererFactory rendererFactory) 
        : base(model)
    {
        _dataManager = dataManager ?? throw new ArgumentNullException(nameof(dataManager));
        _rendererFactory = rendererFactory ?? throw new ArgumentNullException(nameof(rendererFactory));
        _fileReference = new FitsFileReference(model.ImagePath);

        // Inizializziamo il navigatore nello stato "Single"
        _navigator.UpdateStatus(0, 1);
    }

    public async Task InitializeAsync() => await LoadImageAsync();

    // ---------------------------------------------------------------------------
    // CORE RENDERING PIPELINE
    // ---------------------------------------------------------------------------

    private async Task LoadImageAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        
        _loadingCts?.Cancel();
        _loadingCts?.Dispose();
        _loadingCts = new CancellationTokenSource();

        try
        {
            // 1. Recupero dati tramite DataManager (Cache-aware)
            var data = await _dataManager.GetDataAsync(_fileReference.FilePath);
            
            // 2. Creazione del Renderer (usa Header modificato se presente)
            var headerToUse = _fileReference.ModifiedHeader ?? data.Header;
            var newRenderer = _rendererFactory.Create(data.PixelData, headerToUse);
            
            // 3. Swap Atomico (gestito dalla classe base ImageNodeViewModel)
            // Nota: ApplyNewRendererAsync eredita automaticamente VisualizationMode e Contrast
            await ApplyNewRendererAsync(newRenderer);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) 
        { 
            ErrorMessage = $"Errore caricamento: {ex.Message}";
            System.Diagnostics.Debug.WriteLine($"[SingleImageNode] Load Error: {ex}");
        }
        finally { IsLoading = false; }
    }

    // ---------------------------------------------------------------------------
    // GESTIONE DATI E INPUT
    // ---------------------------------------------------------------------------

    public override async Task LoadInputAsync(IEnumerable<FitsFileReference> input)
    {
        var first = input.FirstOrDefault();
        if (first != null)
        {
            _fileReference = first;
            _navigator.UpdateStatus(0, 1); // Reset navigatore se cambiamo file
            await LoadImageAsync();
        }
    }

    public override async Task RefreshDataFromDiskAsync()
    {
        _dataManager.Invalidate(_fileReference.FilePath);
        await LoadImageAsync();
    }

    // ---------------------------------------------------------------------------
    // CLEANUP
    // ---------------------------------------------------------------------------

    protected override void OnRendererSwapping(FitsRenderer newRenderer) => _activeFitsImage = newRenderer;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _navigator.Stop(); // Cleanup preventivo del navigatore
            _loadingCts?.Cancel();
            _loadingCts?.Dispose();
            _activeFitsImage?.Dispose();
        }
        base.Dispose(disposing);
    }
}