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
using KomaLab.ViewModels.Visualization;
using SequenceNavigator = KomaLab.ViewModels.Shared.SequenceNavigator;

namespace KomaLab.ViewModels.Nodes;

/// <summary>
/// Nodo per la gestione di una singola immagine FITS.
/// Aderisce al pattern Async Factory per garantire la robustezza del renderer.
/// Implementa IImageNavigator tramite un SequenceNavigator fisso (1/1).
/// </summary>
public partial class SingleImageNodeViewModel : ImageNodeViewModel
{
    private readonly IFitsDataManager _dataManager;
    private readonly IFitsRendererFactory _rendererFactory;

    // Wrapper persistente per evitare allocazioni inutili e garantire coerenza dei riferimenti
    private readonly List<FitsFileReference> _filesWrapper;
    private FitsFileReference _fileReference;
    
    private CancellationTokenSource? _loadingCts;
    
    private readonly Size _imageSize; // Aggiungi questo campo
    
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
    
    // Binding Proxy: La view deve bindare a ActiveFitsImage per aggiornamenti sicuri
    public override FitsRenderer? ActiveRenderer => ActiveFitsImage;
    
    public override Size NodeContentSize => _imageSize;

    // FIX: Restituiamo sempre la stessa istanza della lista wrapper. 
    // Nessun new[] {}, nessuna copia, nessun riferimento perso.
    public override IReadOnlyList<FitsFileReference> CurrentFiles => _filesWrapper;
    
    public override FitsFileReference? ActiveFile => _fileReference;

    // ---------------------------------------------------------------------------
    // COSTRUTTORE E INIZIALIZZAZIONE
    // ---------------------------------------------------------------------------

    public SingleImageNodeViewModel(
        SingleImageNodeModel model, 
        IFitsDataManager dataManager, 
        IFitsRendererFactory rendererFactory,
        Size imageSize) // <--- Aggiunto
        : base(model)
    {
        _dataManager = dataManager;
        _rendererFactory = rendererFactory;
        _imageSize = imageSize; // <--- Memorizza la dimensione
        
        _fileReference = new FitsFileReference(model.ImagePath);
        _filesWrapper = new List<FitsFileReference> { _fileReference };
        _navigator.UpdateStatus(0, 1);
    }

    public async Task InitializeAsync() => await LoadImageAsync();

    // ---------------------------------------------------------------------------
    // CORE RENDERING PIPELINE (Async Factory Pattern)
    // ---------------------------------------------------------------------------

    private async Task LoadImageAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        
        _loadingCts?.Cancel();
        _loadingCts?.Dispose();
        _loadingCts = new CancellationTokenSource();
        // Nota: Passiamo il token alle operazioni async dove possibile

        try
        {
            // 1. Recupero dati tramite DataManager (Cache-aware)
            var data = await _dataManager.GetDataAsync(_fileReference.FilePath);
            
            // [MODIFICA MEF] Accesso sicuro all'HDU immagine
            // Recuperiamo la prima estensione valida contenente un'immagine
            var imageHdu = data.FirstImageHdu ?? data.PrimaryHdu;

            if (imageHdu == null)
                throw new InvalidOperationException("Il file FITS non contiene estensioni immagine valide.");

            // 2. CREAZIONE & INIZIALIZZAZIONE ATOMICA
            // Se esiste un ModifiedHeader in RAM (es. dall'editor), usiamo quello.
            var headerToUse = _fileReference.ModifiedHeader ?? imageHdu.Header;
            
            // Usiamo i PixelData dell'HDU specifico
            var newRenderer = await _rendererFactory.CreateAsync(imageHdu.PixelData, headerToUse);
            
            // 3. Swap Atomico
            // La classe base gestisce automaticamente l'ereditarietà del contrasto
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
            // Aggiorniamo il riferimento principale
            _fileReference = first;
            
            // Aggiorniamo il wrapper esistente SENZA ricrearlo
            _filesWrapper.Clear();
            _filesWrapper.Add(_fileReference);

            _navigator.UpdateStatus(0, 1); // Reset navigatore se cambiamo file
            await LoadImageAsync();
        }
    }

    public override async Task RefreshDataFromDiskAsync()
    {
        // Invalidiamo la cache dei dati grezzi su disco
        _dataManager.Invalidate(_fileReference.FilePath);
        
        // Ricarichiamo. Nota: LoadImageAsync darà ancora priorità 
        // a _fileReference.ModifiedHeader se presente in RAM.
        await LoadImageAsync();
    }

    // ---------------------------------------------------------------------------
    // CLEANUP
    // ---------------------------------------------------------------------------

    protected override void OnRendererSwapping(FitsRenderer newRenderer)
    {
        // Usiamo il SETTER della proprietà generata per scatenare le notifiche corrette
        ActiveFitsImage = newRenderer;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _navigator.Stop(); // Cleanup preventivo del navigatore
            _loadingCts?.Cancel();
            _loadingCts?.Dispose();
            ActiveFitsImage?.Dispose();
        }
        base.Dispose(disposing);
    }
}