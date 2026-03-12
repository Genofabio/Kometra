using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using Kometra.Models.Fits.Structure;
using Kometra.Models.Visualization;
using Kometra.Services.Fits.Conversion;
using Kometra.Services.Processing.Rendering;
using OpenCvSharp;
using Size = Avalonia.Size;

namespace Kometra.ViewModels.Visualization;

/// <summary>
/// Gestisce il ciclo di vita visuale di un'immagine FITS.
/// Ottimizzato per ridurre l'impronta in RAM tramite bit-depth adattivo (32/64 bit)
/// e scaricamento immediato dei buffer sorgente dopo l'idratazione della matrice scientifica.
/// </summary>
public partial class FitsRenderer : ObservableObject, IDisposable
{
    // --- Rilevamento OS a Runtime ---
    private static readonly bool IsLinux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    // --- Dati Sorgente (Temporanei) ---
    private Array? _rawPixels; 
    private readonly double _bScale;
    private readonly double _bZero;
    private readonly int _width;
    private readonly int _height;
    private readonly FitsBitDepth _targetBitDepth; 

    // --- Dipendenze Core ---
    private readonly IFitsOpenCvConverter _converter;
    private readonly IImagePresentationService _presentationService;

    // --- Stato Interno e Cache ---
    private CancellationTokenSource? _regenerationCts;
    private Mat? _cachedScientificMat; 
    private WriteableBitmap? _backBuffer; 
    private bool _disposedValue;

    // Cache dei requisiti statistici (Mean/StdDev) calcolati una tantum
    private (double Mean, double StdDev)? _presentationRequirements;

    // --- Hook per i Tool ---
    public Action<Mat>? PostProcessAction { get; set; }

    // --- Proprietà Pubbliche ---
    public Size ImageSize => new(_width, _height);
    public bool IsDisposed => _disposedValue; 

    /// <summary> Espone la precisione effettiva utilizzata per il rendering (32 o 64 bit). </summary>
    public FitsBitDepth RenderBitDepth => _targetBitDepth;

    [ObservableProperty] private Bitmap? _image;
    [ObservableProperty] private double _blackPoint;
    [ObservableProperty] private double _whitePoint;
    [ObservableProperty] private VisualizationMode _visualizationMode = VisualizationMode.Linear;

    // =======================================================================
    // CICLO DI VITA
    // =======================================================================

    public FitsRenderer(
        Array pixelData,
        double bScale,
        double bZero,
        FitsBitDepth targetBitDepth, 
        IFitsOpenCvConverter converter,    
        IImagePresentationService presentationService) 
    {
        _rawPixels = pixelData ?? throw new ArgumentNullException(nameof(pixelData));
        _bScale = bScale;
        _bZero = bZero;
        _targetBitDepth = targetBitDepth;
        _converter = converter;
        _presentationService = presentationService;

        _height = _rawPixels.GetLength(0);
        _width = _rawPixels.GetLength(1);
    }

    /// <summary>
    /// Genera la matrice OpenCV e libera immediatamente la memoria dell'array sorgente C#.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_disposedValue || _rawPixels == null) return;

        Console.WriteLine($"\n[FitsRenderer] >>> INIZIO INITIALIZE ASYNC ({_width}x{_height})");
        Console.WriteLine($"[FitsRenderer] Parametri: Scale={_bScale}, Zero={_bZero}, Depth={_targetBitDepth}");

        try
        {
            await Task.Run(() =>
            {
                Console.WriteLine("[FitsRenderer] [Thread Task] Verifica pre-chiamata RawToMat...");
                if (_converter == null) throw new Exception("Il servizio _converter è NULL!");
                
                Console.WriteLine("[FitsRenderer] [Thread Task] Sto per invocare _converter.RawToMat...");
                
                // PUNTO CRITICO: Il salto verso il converter
                _cachedScientificMat = _converter.RawToMat(_rawPixels, _bScale, _bZero, _targetBitDepth);
                
                Console.WriteLine("[FitsRenderer] [Thread Task] _converter.RawToMat è ritornato con successo.");
                
                if (_cachedScientificMat == null) Console.WriteLine("[FitsRenderer] ATTENZIONE: Mat ritornata è NULL!");
                else Console.WriteLine($"[FitsRenderer] Mat creata: {_cachedScientificMat.Width}x{_cachedScientificMat.Height}, Type: {_cachedScientificMat.Type()}");

                // 2. OTTIMIZZAZIONE RAM: Rilasciamo l'array originale
                _rawPixels = null; 
                Console.WriteLine("[FitsRenderer] Array rawPixels rilasciato (null).");
            });

            // 3. Setup iniziale del contrasto (AutoStretch)
            Console.WriteLine("[FitsRenderer] Invocazione ResetThresholdsAsync...");
            await ResetThresholdsAsync(skipRegeneration: true);
            
            Console.WriteLine("[FitsRenderer] Invocazione TriggerRegeneration (Prima Visualizzazione)...");
            await TriggerRegeneration();
            
            Console.WriteLine("[FitsRenderer] <<< INITIALIZE ASYNC COMPLETATO CORRETTAMENTE.\n");
        }
        catch (Exception ex)
        {
            Console.WriteLine("***************************************************");
            Console.WriteLine("[FitsRenderer CRASH] Errore rilevato durante InitializeAsync!");
            Console.WriteLine($"Tipo Eccezione: {ex.GetType().Name}");
            Console.WriteLine($"Messaggio: {ex.Message}");
            if (ex.InnerException != null) 
                Console.WriteLine($"Inner Exception: {ex.InnerException.Message}");
            Console.WriteLine($"Stack Trace: {ex.StackTrace}");
            Console.WriteLine("***************************************************");
        }
    }

    // =======================================================================
    // LOGICA DI DOMINIO (Contrasto Relativo/Sigma)
    // =======================================================================

    public SigmaContrastProfile CaptureSigmaProfile()
    {
        if (_disposedValue || _presentationRequirements == null) 
            return new SigmaContrastProfile(-1.5, 10.0);

        return _presentationService.GetRelativeProfile(
            CaptureContrastProfile(), 
            _presentationRequirements.Value);
    }

    public void ApplyRelativeProfile(SigmaContrastProfile relativeProfile)
    {
        if (_disposedValue || _presentationRequirements == null || relativeProfile == null) return;

        var absoluteProfile = _presentationService.GetAbsoluteProfile(
            relativeProfile, 
            _presentationRequirements.Value);

        ApplyContrastProfile(absoluteProfile);
    }

    public AbsoluteContrastProfile CaptureContrastProfile() => new(BlackPoint, WhitePoint);

    public void ApplyContrastProfile(AbsoluteContrastProfile profile)
    {
        if (_disposedValue || profile == null) return;
        
        BlackPoint = profile.BlackAdu;
        WhitePoint = profile.WhiteAdu;
    }

    public (double Mean, double StdDev) PresentationMetrics => _presentationRequirements ?? (0, 1);

    // =======================================================================
    // RENDERING PIPELINE
    // =======================================================================

    public async Task ResetThresholdsAsync(bool skipRegeneration = false)
    {
        if (_disposedValue || _cachedScientificMat == null) return;

        Console.WriteLine("[FitsRenderer] ResetThresholds: Calcolo PresentationRequirements...");
        _presentationRequirements = await Task.Run(() => 
            _presentationService.GetPresentationRequirements(_cachedScientificMat));
        
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

    partial void OnBlackPointChanged(double value) => _ = TriggerRegeneration();
    partial void OnWhitePointChanged(double value) => _ = TriggerRegeneration();
    partial void OnVisualizationModeChanged(VisualizationMode value) => _ = TriggerRegeneration();

    public void RequestRefresh() => _ = TriggerRegeneration();

    private async Task TriggerRegeneration()
    {
        if (_disposedValue || _cachedScientificMat == null) return;
        
        _regenerationCts?.Cancel();
        _regenerationCts?.Dispose();
        _regenerationCts = new CancellationTokenSource();
        var token = _regenerationCts.Token;

        try { await RegeneratePreviewImageAsync(token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Console.WriteLine($"[FitsRenderer] Errore rigenerazione: {ex.Message}"); }
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

                    if (IsLinux)
                    {
                        Console.WriteLine("[FitsRenderer] [Render] Linux Path: Creazione buffer BGRA...");
                        using var grayMat = new Mat(_height, _width, MatType.CV_8UC1);
                        _presentationService.RenderTo8Bit(_cachedScientificMat, grayMat, BlackPoint, WhitePoint, VisualizationMode);
                        PostProcessAction?.Invoke(grayMat);

                        using var dstMat = Mat.FromPixelData(_height, _width, MatType.CV_8UC4, lockedBuffer.Address, lockedBuffer.RowBytes);
                        Cv2.CvtColor(grayMat, dstMat, ColorConversionCodes.GRAY2BGRA);
                    }
                    else
                    {
                        using var dstMat = Mat.FromPixelData(_height, _width, MatType.CV_8UC1, lockedBuffer.Address, lockedBuffer.RowBytes);
                        _presentationService.RenderTo8Bit(_cachedScientificMat, dstMat, BlackPoint, WhitePoint, VisualizationMode);
                        PostProcessAction?.Invoke(dstMat);
                    }

                }, token);
            }

            if (!token.IsCancellationRequested) SwapBuffer(targetBitmap);
            else RecycleBuffer(targetBitmap);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FitsRenderer] CRASH durante RegeneratePreview: {ex.Message}");
            targetBitmap.Dispose(); 
            throw; 
        }
    }

    // =======================================================================
    // GESTIONE BUFFER E MEMORIA NATIVA
    // =======================================================================

    private WriteableBitmap GetOrCreateBuffer()
    {
        if (_backBuffer != null && _backBuffer.PixelSize.Width == _width && _backBuffer.PixelSize.Height == _height)
        {
            var tmp = _backBuffer; _backBuffer = null; return tmp;
        }

        PixelFormat format = IsLinux ? PixelFormats.Bgra8888 : PixelFormats.Gray8;
        AlphaFormat alpha = IsLinux ? AlphaFormat.Premul : AlphaFormat.Opaque;

        return new WriteableBitmap(new PixelSize(_width, _height), new Vector(96, 96), format, alpha);
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