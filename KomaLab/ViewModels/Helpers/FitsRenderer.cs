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

/// <summary>
/// Record immutabile per trasferire le impostazioni di contrasto tra un renderer e l'altro.
/// Supporta sia modalità assoluta (stessa immagine) che relativa (sigma clipping su immagini diverse).
/// </summary>
public record ContrastProfile(double Val1, double Val2, bool IsAbsolute)
{
    // Helpers semantici per leggere i valori
    public double KBlack => IsAbsolute ? 0 : Val1;
    public double KWhite => IsAbsolute ? 0 : Val2;
    public double Black => IsAbsolute ? Val1 : 0;
    public double White => IsAbsolute ? Val2 : 0;
}

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

    /// <summary>
    /// Cattura lo stato attuale del contrasto. 
    /// Calcola i fattori Sigma (K) basati sulla statistica corrente dell'immagine.
    /// </summary>
    public ContrastProfile CaptureContrastProfile()
    {
        var (mean, sigma) = GetImageStatistics();

        // Se l'immagine è piatta o non valida, usiamo i valori assoluti come fallback
        if (sigma <= 1e-9) 
            return new ContrastProfile(BlackPoint, WhitePoint, IsAbsolute: true);

        // Calcolo fattori K (quante deviazioni standard dalla media)
        double kBlack = (BlackPoint - mean) / sigma;
        double kWhite = (WhitePoint - mean) / sigma;

        return new ContrastProfile(kBlack, kWhite, IsAbsolute: false);
    }

    /// <summary>
    /// Applica un profilo di contrasto a questa immagine.
    /// Se il profilo è relativo, adatta le soglie alla statistica di QUESTA immagine.
    /// </summary>
    public void ApplyContrastProfile(ContrastProfile profile)
    {
        if (_disposedValue) return;

        if (profile.IsAbsolute)
        {
            // Ripristino valori esatti (utile per Undo o ricaricamento stessa immagine)
            BlackPoint = profile.Black;
            WhitePoint = profile.White;
        }
        else
        {
            // Applicazione adattiva (utile per scorrere stack di immagini diverse)
            var (mean, sigma) = GetImageStatistics();
            
            // Applica i fattori K alla nuova distribuzione statistica
            BlackPoint = mean + (profile.KBlack * sigma);
            WhitePoint = mean + (profile.KWhite * sigma);
        }
        // Nota: Il setter delle proprietà triggera automaticamente OnBlackPointChanged -> TriggerRegeneration
    }

    // --- Logica Rendering ---

    partial void OnBlackPointChanged(double value) => _ = TriggerRegeneration();
    partial void OnWhitePointChanged(double value) => _ = TriggerRegeneration();

    private async Task TriggerRegeneration()
    {
        if (_disposedValue) return;
        
        // Debounce / Cancellation del lavoro precedente
        _regenerationCts?.Cancel();
        _regenerationCts = new CancellationTokenSource();
        var token = _regenerationCts.Token;

        try 
        {
            // Piccolo delay per raggruppare modifiche rapide (opzionale, utile per slider fluidi)
            // await Task.Delay(10, token); 
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

        // Allocazione WriteableBitmap
        var writeableBmp = new WriteableBitmap(
            new PixelSize(_imageData.Width, _imageData.Height), 
            new Vector(96, 96),                                 
            PixelFormats.Gray8,                                 
            AlphaFormat.Opaque);                                

        try
        {
            using (var lockedBuffer = writeableBmp.Lock())
            {
                // Cattura variabili locali per thread safety
                var w = _imageData.Width;
                var h = _imageData.Height;
                var bp = BlackPoint;
                var wp = WhitePoint;
                var addr = lockedBuffer.Address;
                var rowBytes = lockedBuffer.RowBytes;
                var mat = _cachedScientificMat;

                await Task.Run(() =>
                {
                    token.ThrowIfCancellationRequested();
                    // Normalizzazione pixel (Scientifico -> 0-255 Display)
                    _fitsService.NormalizeData(mat, w, h, bp, wp, addr, rowBytes);
                }, token);
            }

            if (!token.IsCancellationRequested)
            {
                // Aggiornamento proprietà UI (Main Thread)
                Image = writeableBmp;
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

    public async Task ResetThresholdsAsync(bool skipRegeneration = false)
    {
        if (_disposedValue) return;

        // Calcolo automatico soglie (Auto-Stretch)
        var (newBlack, newWhite) = await Task.Run(() => 
            _converter.CalculateDisplayThresholds(_imageData)
        );

        if (skipRegeneration)
        {
            // Aggiorna campi senza scatenare notifiche/rigenerazione
            SetProperty(ref _blackPoint, newBlack, nameof(BlackPoint));
            SetProperty(ref _whitePoint, newWhite, nameof(WhitePoint));
        }
        else
        {
            // Aggiorna proprietà pubbliche scatenando la rigenerazione
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
                Image?.Dispose(); // Rilascia la bitmap Avalonia
            }
            
            // Risorse non gestite (OpenCV Mat)
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