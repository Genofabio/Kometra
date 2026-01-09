using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia; 
using Avalonia.Media.Imaging; 
using Avalonia.Platform; 
using CommunityToolkit.Mvvm.ComponentModel;
using KomaLab.Models;
using KomaLab.Models.Fits;
using KomaLab.Models.Visualization;
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

    // Modalità di visualizzazione
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
            // Offload su thread background
            _cachedScientificMat = _converter.RawToMat(_imageData);
        });

        // Calcoliamo i default
        await ResetThresholdsAsync(skipRegeneration: true);
        
        // Prima renderizzazione
        await TriggerRegeneration();
    }

    // --- Gestione Profili di Contrasto (LOGICA CORRETTA) ---

    /// <summary>
    /// Cattura lo stato corrente dei livelli come profilo Assoluto (Snapshot).
    /// </summary>
    public ContrastProfile CaptureContrastProfile()
    {
        // Quando l'utente cattura manualmente il profilo, salviamo i valori esatti (ADU).
        // Questo garantisce che riapplicandolo si ottenga esattamente la stessa immagine.
        return new AbsoluteContrastProfile(BlackPoint, WhitePoint);
    }

    /// <summary>
    /// Applica un profilo (Assoluto o Relativo) calcolando i nuovi Black/White Point.
    /// </summary>
    public void ApplyContrastProfile(ContrastProfile profile)
    {
        if (_disposedValue) return;
        if (_cachedScientificMat == null || _cachedScientificMat.IsDisposed) return;

        double newBlack, newWhite;

        switch (profile)
        {
            case AbsoluteContrastProfile abs:
                // Caso semplice: Copia diretta dei valori ADU
                newBlack = abs.BlackADU;
                newWhite = abs.WhiteADU;
                break;

            case RelativeContrastProfile rel:
                // Caso dinamico: Calcola i valori basandosi sul range Min-Max attuale
                // Nota: MinMaxLoc è molto veloce su Mat in memoria
                Cv2.MinMaxLoc(_cachedScientificMat, out double minVal, out double maxVal, out _, out _);
                
                double range = maxVal - minVal;
                
                // Formula: Valore = Min + (Range * Percentile)
                newBlack = minVal + (range * rel.LowerPercentile);
                newWhite = minVal + (range * rel.UpperPercentile);
                break;

            default:
                return; // Profilo sconosciuto
        }

        // Impostiamo le proprietà (questo triggera OnBlackPointChanged -> Rigenerazione)
        // Usiamo SetProperty per notificare la UI
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

        // Crea una WriteableBitmap (buffer CPU condiviso)
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
                var mode = VisualizationMode; 

                await Task.Run(() =>
                {
                    token.ThrowIfCancellationRequested();
                    // Normalizzazione diretta sul buffer della bitmap
                    _fitsService.NormalizeData(mat, w, h, bp, wp, addr, rowBytes, mode);
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

    private void UpdateImage(Bitmap newImage)
    {
        var oldImage = Image;
        Image = newImage;
        oldImage?.Dispose();
    }

    public async Task ResetThresholdsAsync(bool skipRegeneration = false)
    {
        if (_disposedValue) return;

        // Calcolo automatico iniziale (solitamente Min/Max o AutoStretch)
        var (newBlack, newWhite) = await Task.Run(() => 
            _converter.CalculateDisplayThresholds(_imageData)
        );

        if (skipRegeneration)
        {
            // Aggiorna i backing field senza scatenare l'evento PropertyChanged
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