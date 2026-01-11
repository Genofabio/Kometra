using System;
using System.Runtime.InteropServices; // Necessario per Marshal
using System.Threading;
using System.Threading.Tasks;
using Avalonia; 
using Avalonia.Media.Imaging; 
using Avalonia.Platform; 
using CommunityToolkit.Mvvm.ComponentModel;
using KomaLab.Models.Fits;
using KomaLab.Models.Visualization;
using KomaLab.Services.Data;        
using KomaLab.Services.Imaging;     
using OpenCvSharp;
using Size = Avalonia.Size;

namespace KomaLab.ViewModels.Helpers;

// ---------------------------------------------------------------------------
// FILE: FitsRenderer.cs
// RUOLO: Motore di Visualizzazione (Safe Implementation)
// DESCRIZIONE:
// Gestisce la pipeline di rendering real-time:
// Dati Scientifici (Double) -> Pipeline (Stretch, Cutoff) -> Bitmap UI (8-bit).
// Implementazione "Managed" senza blocchi unsafe.
// ---------------------------------------------------------------------------

public partial class FitsRenderer : ObservableObject, IDisposable
{
    // --- Dipendenze Enterprise ---
    private readonly FitsImageData _imageData;
    private readonly IFitsIoService _ioService;         // Sostituisce FitsService
    private readonly IFitsImageDataConverter _converter;
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

    // Modalità di visualizzazione
    [ObservableProperty] private VisualizationMode _visualizationMode = VisualizationMode.Linear;
    
    public bool IsDisposed => _disposedValue; 

    // --- Costruttore ---
    public FitsRenderer(
        FitsImageData imageData, 
        IFitsIoService ioService, 
        IFitsImageDataConverter converter,    
        IImageAnalysisService analysis)  
    {
        _imageData = imageData;
        _ioService = ioService;
        _converter = converter;
        _analysis = analysis;
    }

    public async Task InitializeAsync()
    {
        if (_disposedValue) return;

        await Task.Run(() =>
        {
            // Offload su thread background
            // Conversione Raw -> Mat (Unmanaged Memory)
            _cachedScientificMat = _converter.RawToMat(_imageData);
        });

        // Calcoliamo i default
        await ResetThresholdsAsync(skipRegeneration: true);
        
        // Prima renderizzazione
        await TriggerRegeneration();
    }

    // --- Gestione Profili di Contrasto ---

    public ContrastProfile CaptureContrastProfile()
    {
        return new AbsoluteContrastProfile(BlackPoint, WhitePoint);
    }

    public void ApplyContrastProfile(ContrastProfile profile)
    {
        if (_disposedValue) return;
        if (_cachedScientificMat == null || _cachedScientificMat.IsDisposed) return;

        double newBlack, newWhite;

        switch (profile)
        {
            case AbsoluteContrastProfile abs:
                newBlack = abs.BlackADU;
                newWhite = abs.WhiteADU;
                break;

            case RelativeContrastProfile rel:
                Cv2.MinMaxLoc(_cachedScientificMat, out double minVal, out double maxVal, out _, out _);
                double range = maxVal - minVal;
                newBlack = minVal + (range * rel.LowerPercentile);
                newWhite = minVal + (range * rel.UpperPercentile);
                break;

            default: return;
        }

        BlackPoint = newBlack;
        WhitePoint = newWhite;
    }

    // --- Logica Rendering ---

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
            System.Diagnostics.Debug.WriteLine($"[FitsRenderer] Error regenerating: {ex.Message}");
        }
    }

    private async Task RegeneratePreviewImageAsync(CancellationToken token)
    {
        if (_cachedScientificMat == null || _cachedScientificMat.IsDisposed) return;

        // WriteableBitmap per accesso alla memoria video
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
                var stride = lockedBuffer.RowBytes;
                var mat = _cachedScientificMat;
                var mode = VisualizationMode; 

                await Task.Run(() =>
                {
                    token.ThrowIfCancellationRequested();
                    // Rendering SAFE (usando Marshal invece di unsafe)
                    RenderToBufferSafe(mat, w, h, bp, wp, addr, stride, mode);
                }, token);
            }

            if (!token.IsCancellationRequested)
            {
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
    /// Rendering SAFE: Usa Marshal.Copy per trasferire i dati, evitando blocchi 'unsafe'.
    /// </summary>
    private static void RenderToBufferSafe(Mat source, int w, int h, double bp, double wp, IntPtr destBuffer, int stride, VisualizationMode mode)
    {
        // 1. Setup range
        double range = wp - bp;
        if (Math.Abs(range) < 1e-9) range = 1e-5;
        
        double scale = 1.0 / range;
        double offset = -bp * scale;

        using Mat temp = new Mat();

        // 2. Normalizzazione
        source.ConvertTo(temp, MatType.CV_32FC1, scale, offset);

        // 3. Clipping
        Cv2.Max(temp, 0.0, temp);
        Cv2.Min(temp, 1.0, temp);

        // 4. Stretch Non Lineare
        if (mode == VisualizationMode.SquareRoot)
        {
            Cv2.Sqrt(temp, temp);
        }
        else if (mode == VisualizationMode.Logarithmic)
        {
            Cv2.Add(temp, 1.0, temp);
            Cv2.Log(temp, temp);
            Cv2.Multiply(temp, 1.442695, temp);
        }

        // 5. Conversione a Byte (8-bit)
        using Mat byteMat = new Mat();
        temp.ConvertTo(byteMat, MatType.CV_8UC1, 255.0, 0);

        // 6. Copia nella Bitmap (Metodo SAFE)
        // Creiamo un buffer gestito per una singola riga per minimizzare l'allocazione
        byte[] rowBuffer = new byte[w];

        for (int y = 0; y < h; y++)
        {
            // A. Copia da OpenCV (Unmanaged) -> Buffer Gestito (Managed)
            IntPtr srcRowPtr = byteMat.Ptr(y);
            Marshal.Copy(srcRowPtr, rowBuffer, 0, w);

            // B. Copia da Buffer Gestito (Managed) -> Bitmap Avalonia (Unmanaged)
            // Calcoliamo l'indirizzo della riga di destinazione
            // Nota: IntPtr.Add è il modo sicuro per fare aritmetica dei puntatori
            IntPtr dstRowPtr = IntPtr.Add(destBuffer, y * stride);
            
            Marshal.Copy(rowBuffer, 0, dstRowPtr, w);
        }
    }

    private void UpdateImage(Bitmap newImage)
    {
        var oldImage = Image;
        Image = newImage;
        oldImage?.Dispose();
    }

    public async Task ResetThresholdsAsync(bool skipRegeneration = false)
    {
        if (_disposedValue) return;

        // Usiamo l'analisi per calcolare statistiche di base
        var stats = await Task.Run(() => _analysis.ComputeStatistics(_cachedScientificMat));

        // Auto-Stretch base: Media +/- 2.5 Deviazioni Standard
        double newBlack = stats.Mean - (2.5 * stats.StdDev);
        double newWhite = stats.Mean + (5.0 * stats.StdDev); // Un po' più di highlight

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