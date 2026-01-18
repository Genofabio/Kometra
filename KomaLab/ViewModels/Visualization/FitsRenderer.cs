using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using KomaLab.Models.Visualization;
using KomaLab.Services.Fits.Conversion;
using KomaLab.Services.Processing.Rendering;
using OpenCvSharp;
using Size = Avalonia.Size;

namespace KomaLab.ViewModels.Visualization;

/// <summary>
/// Gestisce il ciclo di vita visuale di un'immagine FITS.
/// Incapsula lo stato del contrasto e la memoria della Bitmap (UI).
/// Custodisce i requisiti radiometrici necessari per la coerenza visuale.
/// </summary>
public partial class FitsRenderer : ObservableObject, IDisposable
{
    // --- Dati Sorgente ---
    private readonly Array _rawPixels;
    private readonly double _bScale;
    private readonly double _bZero;
    private readonly int _width;
    private readonly int _height;

    // --- Dipendenze Core ---
    private readonly IFitsOpenCvConverter _converter;
    private readonly IImagePresentationService _presentationService;

    // --- Stato Interno e Cache ---
    private CancellationTokenSource? _regenerationCts;
    private Mat? _cachedScientificMat;
    private WriteableBitmap? _backBuffer; 
    private bool _disposedValue;

    // Cache dei requisiti radiometrici (Mean, StdDev) necessari per lo stretching
    private (double Mean, double StdDev)? _presentationRequirements;

    // --- Hook per i Tool ---
    public Action<Mat>? PostProcessAction { get; set; }

    // --- Proprietà per la View (Sorgente Unica della Verità) ---
    public Size ImageSize => new(_width, _height);
    public bool IsDisposed => _disposedValue; 

    [ObservableProperty] private Bitmap? _image;
    [ObservableProperty] private double _blackPoint;
    [ObservableProperty] private double _whitePoint;
    [ObservableProperty] private VisualizationMode _visualizationMode = VisualizationMode.Linear;

    // --- Costruttore ---
    public FitsRenderer(
        Array pixelData,
        double bScale,
        double bZero,
        IFitsOpenCvConverter converter,    
        IImagePresentationService presentationService) 
    {
        _rawPixels = pixelData ?? throw new ArgumentNullException(nameof(pixelData));
        _bScale = bScale;
        _bZero = bZero;
        _converter = converter;
        _presentationService = presentationService;

        _height = _rawPixels.GetLength(0);
        _width = _rawPixels.GetLength(1);
    }

    public async Task InitializeAsync()
    {
        if (_disposedValue) return;

        await Task.Run(() =>
        {
            _cachedScientificMat = _converter.RawToMat(_rawPixels, _bScale, _bZero);
        });

        // ResetThresholds popolerà anche la cache dei requisiti
        await ResetThresholdsAsync(skipRegeneration: true);
        await TriggerRegeneration();
    }

    // =======================================================================
    // LOGICA DI DOMINIO (Interfaccia per Nodi e Tool)
    // =======================================================================

    /// <summary>
    /// Genera un profilo adattato per una nuova immagine basandosi sullo stato attuale.
    /// Utilizza i requisiti in cache per garantire performance O(1) durante lo scorrimento.
    /// </summary>
    public AbsoluteContrastProfile GetAdaptedProfileFor(Mat nextMat)
    {
        if (_disposedValue || nextMat == null || _cachedScientificMat == null) 
            return CaptureContrastProfile();
        
        // Se per qualche motivo la cache è vuota, la popoliamo on-demand
        _presentationRequirements ??= _presentationService.GetPresentationRequirements(_cachedScientificMat);

        // Chiediamo al servizio di calcolare il profilo per l'immagine successiva
        return _presentationService.GetAdaptedProfile(
            nextMat, 
            CaptureContrastProfile(), 
            _presentationRequirements.Value);
    }

    public AbsoluteContrastProfile CaptureContrastProfile() => new(BlackPoint, WhitePoint);

    public void ApplyContrastProfile(AbsoluteContrastProfile profile)
    {
        if (_disposedValue || profile == null) return;
        
        // L'aggiornamento di queste proprietà scatena automaticamente TriggerRegeneration()
        BlackPoint = profile.BlackAdu;
        WhitePoint = profile.WhiteAdu;
    }

    public Mat CaptureScientificMat() => _cachedScientificMat?.Clone() ?? throw new InvalidOperationException("Dati non inizializzati.");

    /// <summary>
    /// Calcola (o restituisce dalla cache) i requisiti radiometrici necessari alla presentazione.
    /// </summary>
    private (double Mean, double StdDev) GetPresentationRequirements()
    {
        if (_cachedScientificMat == null || _cachedScientificMat.IsDisposed) return (0, 1);
        
        _presentationRequirements ??= _presentationService.GetPresentationRequirements(_cachedScientificMat);
        return _presentationRequirements.Value;
    }

    public async Task ResetThresholdsAsync(bool skipRegeneration = false)
    {
        if (_disposedValue || _cachedScientificMat == null) return;

        // Popoliamo i requisiti (operazione pesante fatta una volta sola)
        _presentationRequirements = await Task.Run(() => _presentationService.GetPresentationRequirements(_cachedScientificMat));
        
        // Otteniamo il profilo di AutoStretch iniziale
        var profile = _presentationService.GetInitialProfile(_cachedScientificMat);

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

    // =======================================================================
    // RENDERING PIPELINE (Reattiva e Multi-threaded)
    // =======================================================================

    partial void OnBlackPointChanged(double value) => _ = TriggerRegeneration();
    partial void OnWhitePointChanged(double value) => _ = TriggerRegeneration();
    partial void OnVisualizationModeChanged(VisualizationMode value) => _ = TriggerRegeneration();

    public void RequestRefresh() => _ = TriggerRegeneration();

    private async Task TriggerRegeneration()
    {
        if (_disposedValue) return;
        
        _regenerationCts?.Cancel();
        _regenerationCts?.Dispose();
        _regenerationCts = new CancellationTokenSource();
        var token = _regenerationCts.Token;

        try { await RegeneratePreviewImageAsync(token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Debug.WriteLine($"[FitsRenderer] Rendering Error: {ex.Message}"); }
    }

    private async Task RegeneratePreviewImageAsync(CancellationToken token)
    {
        if (_cachedScientificMat == null || _cachedScientificMat.IsDisposed) return;
        
        WriteableBitmap targetBitmap = GetOrCreateBuffer();

        try
        {
            using (var lockedBuffer = targetBitmap.Lock())
            {
                await Task.Run(() =>
                {
                    token.ThrowIfCancellationRequested();
                    
                    using var dstMat = Mat.FromPixelData(_height, _width, MatType.CV_8UC1, lockedBuffer.Address, lockedBuffer.RowBytes);

                    // Delega il rendering stretch all'unico esperto
                    _presentationService.RenderTo8Bit(_cachedScientificMat, dstMat, BlackPoint, WhitePoint, VisualizationMode);

                    // Hook per effetti post-stretch (es. Posterizzazione)
                    PostProcessAction?.Invoke(dstMat);

                }, token);
            }

            if (!token.IsCancellationRequested) SwapBuffer(targetBitmap);
            else RecycleBuffer(targetBitmap);
        }
        catch { targetBitmap.Dispose(); throw; }
    }

    // --- Gestione Buffer e Memoria ---

    private WriteableBitmap GetOrCreateBuffer()
    {
        if (_backBuffer != null && _backBuffer.PixelSize.Width == _width && _backBuffer.PixelSize.Height == _height)
        {
            var tmp = _backBuffer; _backBuffer = null; return tmp;
        }
        return new WriteableBitmap(new PixelSize(_width, _height), new Vector(96, 96), PixelFormats.Gray8, AlphaFormat.Opaque);
    }

    private void RecycleBuffer(WriteableBitmap bmp) => _backBuffer ??= bmp;

    private void SwapBuffer(WriteableBitmap newImage)
    {
        var oldImage = Image as WriteableBitmap;
        Image = newImage;
        if (oldImage != null) RecycleBuffer(oldImage);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposedValue)
        {
            if (disposing)
            {
                _regenerationCts?.Cancel(); _regenerationCts?.Dispose();
                Image?.Dispose(); _backBuffer?.Dispose();
                PostProcessAction = null; 
            }
            _cachedScientificMat?.Dispose();
            _disposedValue = true;
        }
    }

    public void Dispose() { Dispose(true); GC.SuppressFinalize(this); }
}