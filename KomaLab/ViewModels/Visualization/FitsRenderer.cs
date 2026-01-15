using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using KomaLab.Models.Fits;        // Serve solo per il tipo nel costruttore
using KomaLab.Models.Visualization;
using KomaLab.Services.Fits;
using KomaLab.Services.Processing;
using OpenCvSharp;
using Size = Avalonia.Size;

namespace KomaLab.ViewModels.Visualization;

public partial class FitsRenderer : ObservableObject, IDisposable
{
    // --- Dati Sorgente ---
    private readonly Array _rawPixels;
    
    // Parametri scalari essenziali (estratti dall'header e salvati come primitivi)
    private readonly double _bScale;
    private readonly double _bZero;

    // Dimensioni cacheate
    private readonly int _width;
    private readonly int _height;

    // --- Dipendenze ---
    private readonly IFitsOpenCvConverter _converter;
    private readonly IImageAnalysisService _analysis;
    private readonly IMediaExportService _mediaExport; 

    // --- Stato Interno ---
    private CancellationTokenSource? _regenerationCts;
    private Mat? _cachedScientificMat;
    private bool _disposedValue;
    private WriteableBitmap? _backBuffer; 

    // --- Proprietà ---
    public Size ImageSize => new(_width, _height);
    
    // RIMOSSO: public FitsHeader Header => ... (Non serve, non c'è più)

    [ObservableProperty] private Bitmap? _image;
    [ObservableProperty] private double _blackPoint;
    [ObservableProperty] private double _whitePoint;
    [ObservableProperty] private VisualizationMode _visualizationMode = VisualizationMode.Linear;
    
    public bool IsDisposed => _disposedValue; 

    // --- Costruttore ---
    public FitsRenderer(
        Array pixelData,
        FitsHeader header, // Lo usiamo SOLO qui per estrarre i 2 valori, poi muore.
        IFitsOpenCvConverter converter,    
        IImageAnalysisService analysis,
        IMediaExportService mediaExport) 
    {
        _rawPixels = pixelData ?? throw new ArgumentNullException(nameof(pixelData));
        _converter = converter ?? throw new ArgumentNullException(nameof(converter));
        _analysis = analysis ?? throw new ArgumentNullException(nameof(analysis));
        _mediaExport = mediaExport ?? throw new ArgumentNullException(nameof(mediaExport));

        // 1. Calcolo Dimensioni
        _height = _rawPixels.GetLength(0);
        _width = _rawPixels.GetLength(1);

        // 2. Estrazione Immediata dei parametri di scaling (Clean & Simple)
        // Se nulli, usiamo i default FITS (Scale=1, Zero=0)
        if (header != null)
        {
            _bScale = header.GetValue<double>("BSCALE") ?? 1.0;
            _bZero = header.GetValue<double>("BZERO") ?? 0.0;
        }
        else
        {
            _bScale = 1.0;
            _bZero = 0.0;
        }
    }

    public async Task InitializeAsync()
    {
        if (_disposedValue) return;

        // Conversione pesante (CPU Bound) in Background
        await Task.Run(() =>
        {
            // ORA PASSIAMO SOLO I PRIMITIVI
            // Nota: Richiede aggiornamento di IFitsOpenCvConverter
            _cachedScientificMat = _converter.RawToMat(_rawPixels, _bScale, _bZero);
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
            Debug.WriteLine($"[FitsRenderer] Error regenerating: {ex.Message}");
        }
    }

    private async Task RegeneratePreviewImageAsync(CancellationToken token)
    {
        if (_cachedScientificMat == null || _cachedScientificMat.IsDisposed) return;

        WriteableBitmap targetBitmap;
        bool createdNew = false;

        if (_backBuffer != null && 
            _backBuffer.PixelSize.Width == _width && 
            _backBuffer.PixelSize.Height == _height)
        {
            targetBitmap = _backBuffer;
            _backBuffer = null; 
        }
        else
        {
            targetBitmap = new WriteableBitmap(
                new PixelSize(_width, _height), 
                new Vector(96, 96),                                 
                PixelFormats.Gray8,                                 
                AlphaFormat.Opaque);
            createdNew = true;
        }

        try
        {
            using (var lockedBuffer = targetBitmap.Lock())
            {
                // Copia variabili locali per evitare accesso cross-thread
                var w = _width;
                var h = _height;
                var bp = BlackPoint;
                var wp = WhitePoint;
                var addr = lockedBuffer.Address;
                var rowBytes = lockedBuffer.RowBytes;
                var mat = _cachedScientificMat;
                var mode = VisualizationMode; 

                await Task.Run(() =>
                {
                    token.ThrowIfCancellationRequested();
                    _mediaExport.RenderToBuffer(mat, w, h, bp, wp, addr, rowBytes, mode);
                }, token);
            }

            if (!token.IsCancellationRequested)
            {
                SwapBuffer(targetBitmap);
            }
            else
            {
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
        var oldImage = Image as WriteableBitmap;
        Image = newImage;

        if (oldImage != null)
        {
            if (_backBuffer == null) _backBuffer = oldImage;
            else oldImage.Dispose();
        }
    }

    public async Task ResetThresholdsAsync(bool skipRegeneration = false)
    {
        if (_disposedValue || _cachedScientificMat == null) return;

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
                Image?.Dispose();
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