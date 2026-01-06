using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia; 
using Avalonia.Media.Imaging; 
using Avalonia.Platform; 
using CommunityToolkit.Mvvm.ComponentModel;
using KomaLab.Models;
using KomaLab.Services.Data;
using KomaLab.Services.Imaging;
using OpenCvSharp;
using Size = Avalonia.Size;

namespace KomaLab.ViewModels.Helpers;

public partial class FitsRenderer : ObservableObject, IDisposable
{
    // --- Dipendenze ---
    private readonly FitsImageData _imageData;
    private readonly IFitsService _fitsService;
    private readonly IFitsDataConverter _converter;
    private readonly IImageAnalysisService _analysis;

    // --- Stato Interno ---
    private CancellationTokenSource? _regenerationCts;
    private Mat? _cachedScientificMat;
    private bool _disposedValue;

    // --- Proprietà ---
    public Size ImageSize => new(_imageData.Width, _imageData.Height);
    
    public FitsImageData Data => _imageData;

    [ObservableProperty] private Bitmap? _image;
    [ObservableProperty] private double _blackPoint;
    [ObservableProperty] private double _whitePoint;

    // NUOVO: Modalità di visualizzazione (Lineare, Log, ecc.)
    [ObservableProperty] private VisualizationMode _visualizationMode = VisualizationMode.Linear;
    
    public bool IsDisposed => _disposedValue; 

    // --- Costruttore ---
    public FitsRenderer(
        FitsImageData imageData, 
        IFitsService fitsService, 
        IFitsDataConverter converter,    
        IImageAnalysisService analysis)  
    {
        _imageData = imageData;
        _fitsService = fitsService;
        _converter = converter;
        _analysis = analysis;
    }

    public async Task InitializeAsync()
    {
        if (_disposedValue) return;

        await Task.Run(() =>
        {
            // Offload su thread background per non bloccare la UI durante la conversione
            _cachedScientificMat = _converter.RawToMat(_imageData);
        });

        // Calcoliamo i default ma non rigeneriamo subito la bitmap per evitare doppio lavoro
        await ResetThresholdsAsync(skipRegeneration: true);
        
        // Prima renderizzazione effettiva
        await TriggerRegeneration();
    }

    // --- Gestione Profili di Contrasto (SRP Logic) ---

    public ContrastProfile CaptureContrastProfile()
    {
        var (mean, sigma) = GetImageStatistics();

        if (sigma <= 1e-9) 
            return new ContrastProfile(BlackPoint, WhitePoint, IsAbsolute: true);

        double kBlack = (BlackPoint - mean) / sigma;
        double kWhite = (WhitePoint - mean) / sigma;

        return new ContrastProfile(kBlack, kWhite, IsAbsolute: false);
    }

    public void ApplyContrastProfile(ContrastProfile profile)
    {
        if (_disposedValue) return;

        if (profile.IsAbsolute)
        {
            BlackPoint = profile.Black;
            WhitePoint = profile.White;
        }
        else
        {
            var (mean, sigma) = GetImageStatistics();
            BlackPoint = mean + (profile.KBlack * sigma);
            WhitePoint = mean + (profile.KWhite * sigma);
        }
    }

    // --- Logica Rendering ---

    partial void OnBlackPointChanged(double value) => _ = TriggerRegeneration();
    partial void OnWhitePointChanged(double value) => _ = TriggerRegeneration();
    
    // NUOVO: Se cambia il modo, rigeneriamo l'immagine
    partial void OnVisualizationModeChanged(VisualizationMode value) => _ = TriggerRegeneration();

    private async Task TriggerRegeneration()
    {
        if (_disposedValue) return;
        
        // FIX MEMORY LEAK (Resource): Dispose del token precedente
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
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[FitsRenderer] Error regenerating: {ex.Message}");
        }
    }

    private async Task RegeneratePreviewImageAsync(CancellationToken token)
    {
        if (_cachedScientificMat == null || _cachedScientificMat.IsDisposed) return;

        var writeableBmp = new WriteableBitmap(
            new PixelSize(_imageData.Width, _imageData.Height), 
            new Vector(96, 96),                                 
            PixelFormats.Gray8,                                 
            AlphaFormat.Opaque);                                

        try
        {
            using (var lockedBuffer = writeableBmp.Lock())
            {
                var w = _imageData.Width;
                var h = _imageData.Height;
                var bp = BlackPoint;
                var wp = WhitePoint;
                var addr = lockedBuffer.Address;
                var rowBytes = lockedBuffer.RowBytes;
                var mat = _cachedScientificMat;
                
                // Catturiamo il modo corrente per passarlo al thread background
                var mode = VisualizationMode; 

                await Task.Run(() =>
                {
                    token.ThrowIfCancellationRequested();
                    // Modifica qui: Passiamo 'mode' al servizio
                    _fitsService.NormalizeData(mat, w, h, bp, wp, addr, rowBytes, mode);
                }, token);
            }

            if (!token.IsCancellationRequested)
            {
                // FIX MEMORY LEAK (RAM/VRAM): Swap sicuro della bitmap
                UpdateImage(writeableBmp);
            }
            else
            {
                writeableBmp.Dispose();
            }
        }
        catch
        {
            writeableBmp.Dispose();
            throw; 
        }
    }

    /// <summary>
    /// Sostituisce l'immagine corrente assicurandosi di disporre quella vecchia
    /// per liberare immediatamente la memoria video.
    /// </summary>
    private void UpdateImage(Bitmap newImage)
    {
        var oldImage = Image;
        Image = newImage;
        oldImage?.Dispose();
    }

    public async Task ResetThresholdsAsync(bool skipRegeneration = false)
    {
        if (_disposedValue) return;

        var (newBlack, newWhite) = await Task.Run(() => 
            _converter.CalculateDisplayThresholds(_imageData)
        );

        if (skipRegeneration)
        {
            SetProperty(ref _blackPoint, newBlack, nameof(BlackPoint));
            SetProperty(ref _whitePoint, newWhite, nameof(WhitePoint));
        }
        else
        {
            BlackPoint = newBlack;
            WhitePoint = newWhite;
        }
    }
    
    public (double Mean, double StdDev) GetImageStatistics()
    {
        if (_cachedScientificMat == null || _cachedScientificMat.IsDisposed) 
            return (0, 1); 
        
        return _analysis.ComputeStatistics(_cachedScientificMat);
    }

    // --- Gestione Risorse (IDisposable) ---

    public void UnloadData() => Dispose();

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposedValue)
        {
            if (disposing)
            {
                _regenerationCts?.Cancel();
                _regenerationCts?.Dispose();
                
                // Dispose dell'immagine attiva
                Image?.Dispose();
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