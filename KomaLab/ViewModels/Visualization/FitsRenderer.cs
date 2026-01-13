using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using KomaLab.Models.Fits;
using KomaLab.Models.Visualization;
using KomaLab.Services.Fits;
using KomaLab.Services.Processing;
using OpenCvSharp;
using Size = Avalonia.Size;

namespace KomaLab.ViewModels.Visualization;

public partial class FitsRenderer : ObservableObject, IDisposable
{
    // --- Dipendenze ---
    private readonly FitsImageData _imageData;
    private readonly IFitsImageDataConverter _converter;
    private readonly IImageAnalysisService _analysis;
    private readonly IMediaExportService _mediaExport; 

    // --- Stato Interno ---
    private CancellationTokenSource? _regenerationCts;
    private Mat? _cachedScientificMat;
    private bool _disposedValue;

    // OTTIMIZZAZIONE MEMORIA: Back Buffer per il riciclo delle Bitmap
    // Evita di allocare nuova memoria ad ogni frame quando si usano gli slider.
    private WriteableBitmap? _backBuffer; 

    // --- Proprietà ---
    public Size ImageSize => new(_imageData.Width, _imageData.Height);
    public FitsImageData Data => _imageData;

    [ObservableProperty] private Bitmap? _image;
    [ObservableProperty] private double _blackPoint;
    [ObservableProperty] private double _whitePoint;
    [ObservableProperty] private VisualizationMode _visualizationMode = VisualizationMode.Linear;
    
    public bool IsDisposed => _disposedValue; 

    // --- Costruttore ---
    public FitsRenderer(
        FitsImageData imageData, 
        // RIMOSSO: IFitsIoService (non era usato)
        IFitsImageDataConverter converter,    
        IImageAnalysisService analysis,
        IMediaExportService mediaExport) 
    {
        _imageData = imageData ?? throw new ArgumentNullException(nameof(imageData));
        _converter = converter ?? throw new ArgumentNullException(nameof(converter));
        _analysis = analysis ?? throw new ArgumentNullException(nameof(analysis));
        _mediaExport = mediaExport ?? throw new ArgumentNullException(nameof(mediaExport));
    }

    public async Task InitializeAsync()
    {
        if (_disposedValue) return;

        // Conversione pesante (CPU Bound) in Background
        await Task.Run(() =>
        {
            _cachedScientificMat = _converter.RawToMat(_imageData);
        });

        await ResetThresholdsAsync(skipRegeneration: true);
        await TriggerRegeneration();
    }

    public AbsoluteContrastProfile CaptureContrastProfile()
    {
        return new AbsoluteContrastProfile(BlackPoint, WhitePoint);
    }

    public void ApplyContrastProfile(ContrastProfile profile)
    {
        if (_disposedValue) return;

        if (profile is AbsoluteContrastProfile abs)
        {
            // Impostare le proprietà triggera OnChanged -> TriggerRegeneration
            BlackPoint = abs.BlackAdu;
            WhitePoint = abs.WhiteAdu;
        }
    }

    // --- Logica Rendering Reattiva ---

    partial void OnBlackPointChanged(double value) => _ = TriggerRegeneration();
    partial void OnWhitePointChanged(double value) => _ = TriggerRegeneration();
    partial void OnVisualizationModeChanged(VisualizationMode value) => _ = TriggerRegeneration();

    private async Task TriggerRegeneration()
    {
        if (_disposedValue) return;
        
        // Debounce / Cancellation del lavoro precedente
        if (_regenerationCts != null)
        {
            _regenerationCts.Cancel();
            _regenerationCts.Dispose();
        }
        
        _regenerationCts = new CancellationTokenSource();
        var token = _regenerationCts.Token;

        try 
        {
            await RegeneratePreviewImageAsync(token);
        }
        catch (OperationCanceledException) { /* Ignora cancellazioni intenzionali */ }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FitsRenderer] Error regenerating: {ex.Message}");
        }
    }

    private async Task RegeneratePreviewImageAsync(CancellationToken token)
    {
        if (_cachedScientificMat == null || _cachedScientificMat.IsDisposed) return;

        // 1. OTTIMIZZAZIONE: Recupera o crea il buffer di destinazione
        WriteableBitmap targetBitmap;
        bool createdNew = false;

        // Se abbiamo un backbuffer della dimensione giusta, usiamolo
        if (_backBuffer != null && 
            _backBuffer.PixelSize.Width == _imageData.Width && 
            _backBuffer.PixelSize.Height == _imageData.Height)
        {
            targetBitmap = _backBuffer;
            _backBuffer = null; // Lo "preleviamo" dalla scorta
        }
        else
        {
            // Altrimenti allochiamo (solo al primo avvio o se cambia size)
            targetBitmap = new WriteableBitmap(
                new PixelSize(_imageData.Width, _imageData.Height), 
                new Vector(96, 96),                                 
                PixelFormats.Gray8,                                 
                AlphaFormat.Opaque);
            createdNew = true;
        }

        try
        {
            // 2. Rendering nel Buffer (Lock memoria video)
            using (var lockedBuffer = targetBitmap.Lock())
            {
                var w = _imageData.Width;
                var h = _imageData.Height;
                var bp = BlackPoint;
                var wp = WhitePoint;
                var addr = lockedBuffer.Address;
                var rowBytes = lockedBuffer.RowBytes;
                var mat = _cachedScientificMat;
                var mode = VisualizationMode; 

                // Eseguiamo il loop sui pixel in un thread separato per non bloccare la UI
                await Task.Run(() =>
                {
                    token.ThrowIfCancellationRequested();
                    _mediaExport.RenderToBuffer(mat, w, h, bp, wp, addr, rowBytes, mode);
                }, token);
            }

            // 3. Swap dei Buffer (Thread UI)
            if (!token.IsCancellationRequested)
            {
                SwapBuffer(targetBitmap);
            }
            else
            {
                // Se cancellato, rimettiamo il buffer nella scorta (o lo buttiamo se nuovo)
                if (!createdNew) _backBuffer = targetBitmap;
                else targetBitmap.Dispose();
            }
        }
        catch
        {
            targetBitmap.Dispose();
            throw; 
        }
    }

    private void SwapBuffer(Bitmap newImage)
    {
        // Salva l'immagine corrente (che sta per essere tolta dallo schermo)
        var oldImage = Image as WriteableBitmap;

        // Aggiorna la UI
        Image = newImage;

        // RICICLO: La vecchia immagine diventa il nuovo BackBuffer
        // Invece di farla morire nel GC, la teniamo per il prossimo frame.
        if (oldImage != null)
        {
            if (_backBuffer == null) 
            {
                _backBuffer = oldImage;
            }
            else 
            {
                // Caso raro: se avevamo già un backbuffer (race condition?), puliamo il vecchio
                oldImage.Dispose();
            }
        }
    }

    public async Task ResetThresholdsAsync(bool skipRegeneration = false)
    {
        if (_disposedValue || _cachedScientificMat == null) return;

        // FIX: Nome metodo aggiornato
        var profile = await Task.Run(() => 
            _analysis.CalculateAutoStretchProfile(_cachedScientificMat)
        );

        if (skipRegeneration)
        {
            SetProperty(ref _blackPoint, profile.BlackAdu, nameof(BlackPoint));
            SetProperty(ref _whitePoint, profile.WhiteAdu, nameof(WhitePoint));
        }
        else
        {
            // Questo triggera la rigenerazione automatica
            BlackPoint = profile.BlackAdu;
            WhitePoint = profile.WhiteAdu;
        }
    }
    
    public (double Mean, double StdDev) GetImageStatistics()
    {
        if (_cachedScientificMat == null || _cachedScientificMat.IsDisposed) 
            return (0, 1); 
        
        return _analysis.ComputeStatistics(_cachedScientificMat);
    }

    public void UnloadData() => Dispose();

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposedValue)
        {
            if (disposing)
            {
                _regenerationCts?.Cancel();
                _regenerationCts?.Dispose();
                
                // Pulisci l'immagine attiva
                Image?.Dispose();
                
                // Pulisci anche il buffer di scorta!
                _backBuffer?.Dispose();
            }
            
            if (_cachedScientificMat != null && !_cachedScientificMat.IsDisposed)
            {
                _cachedScientificMat.Dispose();
                _cachedScientificMat = null;
            }
            _disposedValue = true;
        }
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}