using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using KomaLab.Models;
using KomaLab.Services;
using OpenCvSharp;
using Size = Avalonia.Size;

namespace KomaLab.ViewModels.Helpers;

/// <summary>
/// Questo ViewModel non è un "Nodo".
/// È un "motore" riutilizzabile che gestisce lo stato di visualizzazione
/// (Black/White, Bitmap) di un singolo modello di dati FITS (FitsImageData).
/// </summary>
public partial class FitsRenderer : ObservableObject
{
    // --- Campi ---
    private readonly FitsImageData _imageData; 
    private readonly IFitsService _fitsService; 
    private readonly IImageProcessingService _processingService;
    
    public Size ImageSize => _imageData.ImageSize;

    // --- Campo per l'Ottimizzazione ---
    private CancellationTokenSource? _regenerationCts;

    // --- Proprietà (Stato dell'Immagine) ---
    
    [ObservableProperty]
    private Bitmap? _image;

    [ObservableProperty]
    private double _blackPoint;

    [ObservableProperty]
    private double _whitePoint;
    
    private Mat? _cachedScientificMat;

    // --- Costruttore ---
    
    public FitsRenderer(
        FitsImageData imageData, 
        IFitsService fitsService, 
        IImageProcessingService processingService)
    {
        _imageData = imageData;
        _fitsService = fitsService;
        _processingService = processingService;
    }
    
    /// <summary>
    /// Avvia la prima generazione dell'immagine e calcola le soglie iniziali.
    /// </summary>
    public async Task InitializeAsync()
    {
        // 1. Esegui il lavoro pesante (BZERO + Mat allocation) una sola volta
        await Task.Run(() =>
        {
            // NOTA: Qui chiamiamo LoadFitsDataAsMat, che contiene la logica BZERO.
            _cachedScientificMat = _processingService.LoadFitsDataAsMat(_imageData);
        });

        // 2. Calcola le soglie usando i dati RAW (corretto da CalculateClippedThresholds)
        var (newBlack, newWhite) = await Task.Run(() => 
            _processingService.CalculateClippedThresholds(_imageData)
        );
    
        // 3. Imposta le proprietà e attiva il primo render
        SetProperty(ref _blackPoint, newBlack, nameof(BlackPoint));
        SetProperty(ref _whitePoint, newWhite, nameof(WhitePoint));

        await TriggerRegeneration(); // Ora il trigger userà la cache
    }
    
    // --- Logica di Rigenerazione ---
    partial void OnBlackPointChanged(double value) => TriggerRegeneration();
    partial void OnWhitePointChanged(double value) => TriggerRegeneration();

    /// <summary>
    /// Avvia una rigenerazione "debounced" (anti-sfarfallio).
    /// </summary>
    private Task TriggerRegeneration()
    {
        _regenerationCts?.Cancel();
        _regenerationCts = new CancellationTokenSource();
        var token = _regenerationCts.Token;
        
        return RegeneratePreviewImageAsync(token);
    }

    /// <summary>
    /// Rigenera il Bitmap usando i dati grezzi e le soglie correnti.
    /// (Versione ottimizzata "zero-copy").
    /// </summary>
    private async Task RegeneratePreviewImageAsync(CancellationToken token)
    {
        // Verifica se la Matrice CACHED è disponibile
        if (_cachedScientificMat == null || _cachedScientificMat.Empty())
        {
            // Se la cache non è pronta, usciamo (questo non dovrebbe accadere dopo InitializeAsync)
            return; 
        }
    
        var writeableBmp = new WriteableBitmap(
            new PixelSize((int)_imageData.ImageSize.Width, (int)_imageData.ImageSize.Height),
            new Vector(96, 96),
            PixelFormats.Gray8, AlphaFormat.Opaque);

        try
        {
            using (var lockedBuffer = writeableBmp.Lock())
            {
                // Passa il puntatore al Task in background
                await Task.Run(() =>
                {
                    token.ThrowIfCancellationRequested();
            
                    // --- NUOVA CHIAMATA VELOCE ---
                    // Invece di passare i dati RAW e l'Header, passiamo la Matrice CACHED.
                    _fitsService.NormalizeData(
                        _cachedScientificMat, // Passiamo la Matrice (già corretta con BZERO)
                        (int)_imageData.ImageSize.Width, 
                        (int)_imageData.ImageSize.Height,
                        BlackPoint, 
                        WhitePoint,
                        lockedBuffer.Address,
                        lockedBuffer.RowBytes); 
                }, token);
            }
    
            if (token.IsCancellationRequested)
            {
                writeableBmp.Dispose();
                return;
            }
        
            Image = writeableBmp;
        }
        catch (OperationCanceledException)
        {
            writeableBmp.Dispose();
        }
        catch (Exception ex)
        {
            // Nota: Utilizziamo Debug.WriteLine invece di un'altra variabile per coerenza
            System.Diagnostics.Debug.WriteLine($"Errore durante la rigenerazione dell'immagine: {ex.Message}");
            writeableBmp.Dispose();
        }
    }
    
    /// <summary>
    /// Ricalcola le soglie ottimali dai dati grezzi
    /// e aggiorna questo ViewModel (e rigenera l'immagine).
    /// Chiamato dal pulsante "Reset" della UI.
    /// </summary>
    public async Task ResetThresholdsAsync()
    {
        // 1. Chiama il servizio di processing
        var (newBlack, newWhite) = await Task.Run(() => 
            _processingService.CalculateClippedThresholds(_imageData)
        );
        
        // 2. Aggiorna le proprietà.
        //    Questo attiverà automaticamente On...Changed
        //    e quindi TriggerRegeneration().
        BlackPoint = newBlack;
        WhitePoint = newWhite;
    }
    
    /// <summary>
    /// Pulisce il Bitmap per liberare RAM.
    /// </summary>
    public void UnloadData()
    {
        _regenerationCts?.Cancel(); 
        Image?.Dispose();
        Image = null;

        // IMPORTANTE: Libera la Matrice OpenCV
        _cachedScientificMat?.Dispose(); 
        _cachedScientificMat = null;
    }
    
    /// <summary>
    /// Calcola statistiche rapide sulla matrice in cache per permettere il Sigma Locking.
    /// </summary>
    public (double Mean, double StdDev) GetImageStatistics()
    {
        if (_cachedScientificMat == null || _cachedScientificMat.Empty()) 
            return (0, 1);

        using var meanMat = new Mat();
        using var stdDevMat = new Mat();
    
        // --- FIX PER I NaN ---
        // 1. Creiamo una maschera. 
        // OpenCV Compare con EQ: 
        // - Se pixel è numero valido: Valido == Valido -> TRUE (255)
        // - Se pixel è NaN: NaN == NaN -> FALSE (0)
        using var mask = new Mat();
        Cv2.Compare(_cachedScientificMat, _cachedScientificMat, mask, CmpType.EQ);
    
        // 2. Calcoliamo statistiche usando la maschera
        Cv2.MeanStdDev(_cachedScientificMat, meanMat, stdDevMat, mask);
        // ---------------------

        double mean = meanMat.Get<double>(0, 0);
        double std = stdDevMat.Get<double>(0, 0);
    
        // Protezione extra se l'immagine è tutta NaN
        if (double.IsNaN(mean)) mean = 0;
        if (double.IsNaN(std) || std < 1e-9) std = 1.0; 

        return (mean, std);
    }
}