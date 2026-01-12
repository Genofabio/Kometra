using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;          
using Avalonia.Media.Imaging; 
using Avalonia.Platform;      
using Avalonia.Threading;     
using CommunityToolkit.Mvvm.ComponentModel;
using KomaLab.Models.Fits;
using KomaLab.Models.Visualization;
using KomaLab.Services.Fits;
using KomaLab.Services.Processing;
using OpenCvSharp;
using Size = Avalonia.Size;

namespace KomaLab.ViewModels.Visualization;

// ---------------------------------------------------------------------------
// FILE: FitsRenderer.cs
// RUOLO: ViewModel di Rendering (High Performance & Low Memory)
// DESCRIZIONE:
// Gestisce la visualizzazione FITS utilizzando una strategia a buffer unico
// riutilizzabile per minimizzare l'impatto sulla memoria (RAM/GC).
// Utilizza un RenderScheduler per garantire la thread-safety.
// ---------------------------------------------------------------------------

public partial class FitsRenderer : ObservableObject, IDisposable
{
    // --- Dipendenze ---
    private readonly FitsImageData _imageData;
    private readonly IFitsImageDataConverter _converter;
    private readonly IImageAnalysisService _analysis;
    
    // --- Infrastruttura ---
    private readonly RenderScheduler _scheduler = new();

    // --- Stato Memoria (Buffer Riutilizzabili) ---
    private Mat? _scientificMat;            // Source Data (Read-Only dopo init)
    private byte[]? _sharedPixelBuffer;     // Intermediate RAM Buffer (Thread-Safe via Scheduler)
    private WriteableBitmap? _renderTarget; // Video Memory (Bindata alla UI)
    
    private bool _disposed;

    // --- Proprietà Esposte ---
    public Size ImageSize => new(_imageData.Width, _imageData.Height);
    public FitsImageData Data => _imageData;
    public bool IsDisposed => _disposed;

    [ObservableProperty] private Bitmap? _image;
    [ObservableProperty] private double _blackPoint;
    [ObservableProperty] private double _whitePoint;
    [ObservableProperty] private VisualizationMode _visualizationMode = VisualizationMode.Linear;

    // --- Costruttore ---
    public FitsRenderer(
        FitsImageData imageData,
        IFitsIoService ioService,
        IFitsImageDataConverter converter,
        IImageAnalysisService analysis)
    {
        _imageData = imageData ?? throw new ArgumentNullException(nameof(imageData));
        _converter = converter ?? throw new ArgumentNullException(nameof(converter));
        _analysis = analysis ?? throw new ArgumentNullException(nameof(analysis));
    }

    // --- Inizializzazione ---
    public async Task InitializeAsync()
    {
        if (_disposed) return;

        // 1. Allocazione Dati Sorgente (Una tantum)
        _scientificMat = await Task.Run(() => _converter.RawToMat(_imageData)).ConfigureAwait(false);

        // 2. Allocazione Buffer RAM (Una tantum)
        int pixelCount = _imageData.Width * _imageData.Height;
        _sharedPixelBuffer = new byte[pixelCount];
        
        // 3. Allocazione Bitmap Video (Una tantum)
        // Usiamo PixelFormats.Gray8 per compatibilità e performance
        _renderTarget = new WriteableBitmap(
            new PixelSize(_imageData.Width, _imageData.Height),
            new Vector(96, 96),
            PixelFormats.Gray8, 
            AlphaFormat.Opaque);

        Image = _renderTarget;

        // 4. Avvio Pipeline
        await ResetThresholdsAsync(skipRender: true).ConfigureAwait(false);
        RequestRender();
    }

    // --- Gestione Profili Contrasto ---
    public ContrastProfile CaptureContrastProfile() => new AbsoluteContrastProfile(BlackPoint, WhitePoint);

    public void ApplyContrastProfile(ContrastProfile profile)
    {
        if (_disposed || _scientificMat == null) return;

        double newBlack, newWhite;
        switch (profile)
        {
            case AbsoluteContrastProfile abs: (newBlack, newWhite) = (abs.BlackADU, abs.WhiteADU); break;
            case RelativeContrastProfile rel:
                Cv2.MinMaxLoc(_scientificMat, out double min, out double max, out _, out _);
                double r = max - min;
                newBlack = min + (r * rel.LowerPercentile);
                newWhite = min + (r * rel.UpperPercentile);
                break;
            default: return;
        }
        BlackPoint = newBlack;
        WhitePoint = newWhite;
    }

    #region Rendering Pipeline

    // Trigger automatici
    partial void OnBlackPointChanged(double _) => RequestRender();
    partial void OnWhitePointChanged(double _) => RequestRender();
    partial void OnVisualizationModeChanged(VisualizationMode _) => RequestRender();

    private void RequestRender()
    {
        if (_disposed || _scientificMat == null || _sharedPixelBuffer == null || _renderTarget == null)
            return;

        // Delega allo scheduler.
        // Il semaforo interno garantisce che _sharedPixelBuffer sia scritto da un solo thread alla volta.
        _ = _scheduler.RunAsync(async token =>
        {
            try 
            {
                // FASE 1: Calcolo CPU (Background)
                await Task.Run(() =>
                {
                    FitsRenderPipeline.RenderToBuffer(
                        _scientificMat,
                        _sharedPixelBuffer,
                        _imageData.Width,
                        _imageData.Height,
                        BlackPoint,
                        WhitePoint,
                        VisualizationMode);
                }, token).ConfigureAwait(false);

                if (token.IsCancellationRequested) return;

                // FASE 2: Upload GPU (UI Thread)
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (_disposed || _renderTarget == null) return;

                    using (var fb = _renderTarget.Lock())
                    {
                        Marshal.Copy(_sharedPixelBuffer, 0, fb.Address, _sharedPixelBuffer.Length);
                    }

                    // Notifica forzata: l'oggetto Image è lo stesso, ma il contenuto è cambiato.
                    OnPropertyChanged(nameof(Image)); 
                    
                }, DispatcherPriority.Render);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FitsRenderer] Render Failed: {ex.Message}");
            }
        });
    }

    #endregion

    #region Analysis & Stats

    public async Task ResetThresholdsAsync(bool skipRender)
    {
        if (_scientificMat == null) return;

        var (newBlack, newWhite) = await Task.Run(() => 
            _analysis.CalculateAutoStretchLevels(_scientificMat))
            .ConfigureAwait(false);

        if (skipRender)
        {
            // CORREZIONE: Aggiorniamo i campi privati (_backingFields) per EVITARE
            // che scattino i trigger automatici (OnBlackPointChanged -> RequestRender).
            _blackPoint = newBlack;
            _whitePoint = newWhite;
            
            // Notifichiamo la UI manualmente (senza triggerare il render)
            OnPropertyChanged(nameof(BlackPoint));
            OnPropertyChanged(nameof(WhitePoint));
        }
        else
        {
            // Qui invece usiamo le proprietà pubbliche PERCHÉ VOGLIAMO il trigger
            BlackPoint = newBlack;
            WhitePoint = newWhite;
        }
    }

    public (double Mean, double StdDev) GetImageStatistics()
    {
        if (_scientificMat == null || _scientificMat.IsDisposed) return (0, 1);
        return _analysis.ComputeStatistics(_scientificMat);
    }

    #endregion

    #region IDisposable

    public void UnloadData() => Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _scheduler.Dispose();
        _scientificMat?.Dispose();
        _renderTarget?.Dispose();
        _image = null;
        
        GC.SuppressFinalize(this);
    }

    #endregion
}

// =========================================================
// REGION: HELPER CLASSES (Internal)
// =========================================================

/// <summary>
/// Gestore della concorrenza per il rendering.
/// Implementa un meccanismo di "Debounce" e "Mutual Exclusion" per proteggere
/// il buffer condiviso da accessi concorrenti.
/// </summary>
internal sealed class RenderScheduler : IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private CancellationTokenSource? _cts;

    public async Task RunAsync(Func<CancellationToken, Task> renderAction)
    {
        // Cancella task precedente
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        // Attende accesso esclusivo
        try
        {
            await _semaphore.WaitAsync(token).ConfigureAwait(false);
            if (!token.IsCancellationRequested)
            {
                await renderAction(token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* Ignora */ }
        finally
        {
            // Rilascia solo se il semaforo è stato acquisito correttamente
            if (_semaphore.CurrentCount == 0) _semaphore.Release();
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _semaphore.Dispose();
    }
}

/// <summary>
/// Pipeline di elaborazione matematica pura (Stateless).
/// Esegue le operazioni OpenCV per trasformare i dati Raw (Double) in Pixel (Byte).
/// </summary>
internal static class FitsRenderPipeline
{
    public static void RenderToBuffer(
        Mat source,
        byte[] destBuffer,
        int width,
        int height,
        double black,
        double white,
        VisualizationMode mode)
    {
        // 1. Setup Range
        double range = Math.Max(white - black, 1e-5);
        double scale = 1.0 / range;
        double offset = -black * scale;

        using Mat temp = new();
        
        // 2. Normalizzazione (Float 32-bit)
        source.ConvertTo(temp, MatType.CV_32FC1, scale, offset);
        Cv2.Max(temp, 0, temp);
        Cv2.Min(temp, 1, temp);

        // 3. Stretch Non-Lineare
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

        // 4. Quantizzazione (8-bit)
        using Mat byteMat = new();
        temp.ConvertTo(byteMat, MatType.CV_8UC1, 255);

        // 5. Scrittura nel Buffer Condiviso
        Marshal.Copy(byteMat.Data, destBuffer, 0, destBuffer.Length);
    }
}