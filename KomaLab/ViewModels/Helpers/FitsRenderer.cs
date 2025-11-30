using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia; 
using Avalonia.Media.Imaging; 
using Avalonia.Platform; 
using CommunityToolkit.Mvvm.ComponentModel;
using KomaLab.Models;
using KomaLab.Services;
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

    // --- Costruttore Aggiornato ---
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
            // Usa il Converter per creare la Matrice OpenCV
            _cachedScientificMat = _converter.RawToMat(_imageData);
        });

        await ResetThresholdsAsync(skipRegeneration: true);
        await TriggerRegeneration();
    }

    // --- Logica Rendering ---

    partial void OnBlackPointChanged(double value) => _ = TriggerRegeneration();
    partial void OnWhitePointChanged(double value) => _ = TriggerRegeneration();

    private async Task TriggerRegeneration()
    {
        if (_disposedValue) return;
        _regenerationCts?.Cancel();
        _regenerationCts = new CancellationTokenSource();
        
        try 
        {
            await RegeneratePreviewImageAsync(_regenerationCts.Token);
        }
        catch (OperationCanceledException) { }
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

                await Task.Run(() =>
                {
                    token.ThrowIfCancellationRequested();
                    // IFitsService mantiene la responsabilità della visualizzazione
                    _fitsService.NormalizeData(mat, w, h, bp, wp, addr, rowBytes);
                }, token);
            }

            if (!token.IsCancellationRequested)
            {
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

        // Usa il Converter per calcolare le soglie (sampling veloce)
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
    
    public (double Mean, double StdDev) GetImageStatistics()
    {
        if (_cachedScientificMat == null || _cachedScientificMat.IsDisposed) 
            return (0, 1); 
        
        // Usa AnalysisService per calcoli matematici
        return _analysis.ComputeStatistics(_cachedScientificMat);
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}