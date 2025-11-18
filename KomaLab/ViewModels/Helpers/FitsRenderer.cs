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

    // --- Costruttore ---
    
    public FitsRenderer(
        FitsImageData imageData, 
        IFitsService fitsService, 
        IImageProcessingService processingService)
    {
        _imageData = imageData;
        _fitsService = fitsService;
        _processingService = processingService;
        
        // Le soglie vengono impostate da InitializeAsync
    }
    
    /// <summary>
    /// Avvia la prima generazione dell'immagine e calcola le soglie iniziali.
    /// </summary>
    public async Task InitializeAsync()
    {
        // 1. Calcola le soglie
        var (newBlack, newWhite) = await Task.Run(() => 
            _processingService.CalculateClippedThresholds(_imageData)
        );
        
        // 2. Imposta le proprietà (usando SetProperty per evitare
        //    di chiamare TriggerRegeneration() inutilmente)
        SetProperty(ref _blackPoint, newBlack, nameof(BlackPoint));
        SetProperty(ref _whitePoint, newWhite, nameof(WhitePoint));

        // 3. ORA avvia la prima rigenerazione e ATTENDILA
        await TriggerRegeneration();
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
                
                    _fitsService.NormalizeData(
                        _imageData.RawData, _imageData.FitsHeader,
                        (int)_imageData.ImageSize.Width, (int)_imageData.ImageSize.Height,
                        BlackPoint, WhitePoint,
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
            writeableBmp.Dispose(); // Pulisci il bitmap che non useremo
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Errore durante la rigenerazione dell'immagine: {ex.Message}");
            writeableBmp.Dispose(); // Pulisci
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
    }
}