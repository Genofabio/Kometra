using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices; 
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KomaLab.Models.Fits;
using KomaLab.Models.Processing;
using KomaLab.Models.Processing.Masking;
using KomaLab.Services.Factories;
using KomaLab.Services.Fits;
using KomaLab.Services.Processing.Batch;
using KomaLab.Services.Processing.Coordinators;
using KomaLab.ViewModels.Visualization;
using SequenceNavigator = KomaLab.ViewModels.Shared.SequenceNavigator;

namespace KomaLab.ViewModels.ImageProcessing;

public partial class StarMaskingViewModel : ObservableObject, IDisposable
{
    private readonly IMaskingCoordinator _coordinator;
    private readonly IFitsDataManager _dataManager;
    private readonly IFitsRendererFactory _rendererFactory;
    
    private readonly List<FitsFileReference> _files;
    private CancellationTokenSource? _previewCts;

    // [AGGIUNTO] Evento per dire alla View di centrare l'immagine
    public event Action? RequestFitToScreen;

    public SequenceNavigator Navigator { get; } = new();
    public AlignmentImageViewport Viewport { get; } = new();

    private readonly MaskingParameters _params = new();

    #region Observable Properties

    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(IsInteractionEnabled))]
    private bool _isBusy;

    [ObservableProperty] private string _statusMessage = "Pronto.";
    
    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(IsInteractionEnabled))]
    private bool _isProcessingBatch;

    [ObservableProperty] private FitsRenderer? _activeRenderer;
    
    [ObservableProperty] private Bitmap? _starMaskOverlay;
    [ObservableProperty] private Bitmap? _cometMaskOverlay;

    [ObservableProperty] private bool _showStarMask = true;
    [ObservableProperty] private bool _showCometMask = true;

    // --- Parametri Proxy ---

    public double CometThreshold
    {
        get => _params.CometThresholdSigma;
        set { if (SetProperty(_params.CometThresholdSigma, value, _params, (u, n) => u.CometThresholdSigma = n)) TriggerPreview(); }
    }

    public int CometDilation
    {
        get => _params.CometDilation;
        set { if (SetProperty(_params.CometDilation, value, _params, (u, n) => u.CometDilation = n)) TriggerPreview(); }
    }

    public double StarThreshold
    {
        get => _params.StarThresholdSigma;
        set { if (SetProperty(_params.StarThresholdSigma, value, _params, (u, n) => u.StarThresholdSigma = n)) TriggerPreview(); }
    }

    public int StarDilation
    {
        get => _params.StarDilation;
        set { if (SetProperty(_params.StarDilation, value, _params, (u, n) => u.StarDilation = n)) TriggerPreview(); }
    }

    // --- Radiometria ---
    public double BlackPoint
    {
        get => ActiveRenderer?.BlackPoint ?? 0;
        set { if (ActiveRenderer != null) { ActiveRenderer.BlackPoint = value; OnPropertyChanged(); } }
    }

    public double WhitePoint
    {
        get => ActiveRenderer?.WhitePoint ?? 65535;
        set { if (ActiveRenderer != null) { ActiveRenderer.WhitePoint = value; OnPropertyChanged(); } }
    }

    public bool IsInteractionEnabled => !IsBusy && !IsProcessingBatch;
    public string CurrentImageText => $"{Navigator.DisplayIndex} / {Navigator.TotalCount}";

    public List<string>? ResultPaths { get; private set; }
    public bool DialogResult { get; private set; }
    public event Action? RequestClose;

    #endregion

    public StarMaskingViewModel(
        List<FitsFileReference> files,
        IMaskingCoordinator coordinator,
        IFitsDataManager dataManager,
        IFitsRendererFactory rendererFactory)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _dataManager = dataManager ?? throw new ArgumentNullException(nameof(dataManager));
        _rendererFactory = rendererFactory ?? throw new ArgumentNullException(nameof(rendererFactory));

        Navigator.UpdateStatus(0, _files.Count);
        Navigator.IndexChanged += async (s, i) => await LoadImageAsync(i);

        _ = LoadImageAsync(0);
    }

    private async Task LoadImageAsync(int index)
    {
        if (index < 0 || index >= _files.Count) return;
        
        StarMaskOverlay = null;
        CometMaskOverlay = null;
        
        try
        {
            var file = _files[index];
            var data = await _dataManager.GetDataAsync(file.FilePath);
            var hdu = data.FirstImageHdu ?? data.PrimaryHdu;

            if (hdu == null) throw new InvalidOperationException("Immagine non valida.");

            var newRenderer = await _rendererFactory.CreateAsync(hdu.PixelData, file.ModifiedHeader ?? hdu.Header);

            if (ActiveRenderer != null)
            {
                var style = ActiveRenderer.CaptureSigmaProfile();
                newRenderer.ApplyRelativeProfile(style);
                ActiveRenderer.Dispose();
            }
            
            ActiveRenderer = newRenderer;
            Viewport.ImageSize = ActiveRenderer.ImageSize;
            
            OnPropertyChanged(nameof(ActiveRenderer));
            OnPropertyChanged(nameof(BlackPoint));
            OnPropertyChanged(nameof(WhitePoint));
            OnPropertyChanged(nameof(CurrentImageText));

            // [MODIFICA] Ora che l'immagine c'è, diciamo alla View di adattare lo zoom
            RequestFitToScreen?.Invoke();

            TriggerPreview();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Errore: {ex.Message}";
        }
    }

    private async void TriggerPreview()
    {
        if (ActiveRenderer == null) return;

        _previewCts?.Cancel();
        _previewCts = new CancellationTokenSource();
        var token = _previewCts.Token;

        try
        {
            await Task.Delay(100, token);
            await CalculatePreviewInternalAsync(token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => StatusMessage = $"Errore: {ex.Message}");
        }
    }

    private async Task CalculatePreviewInternalAsync(CancellationToken token)
    {
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() => StatusMessage = "Calcolo maschere...");

            var currentFile = _files[Navigator.CurrentIndex];
            
            var (rawStar, rawComet) = await _coordinator.CalculatePreviewAsync(currentFile, _params, token);

            if (token.IsCancellationRequested) return;

            var cometColor = Color.Parse("#FFEA7B");
            var starColor = Color.Parse("#567FFF");

            var starBmp = RawMaskToOverlayBitmapSafe(rawStar, starColor, 0.8);
            var cometBmp = RawMaskToOverlayBitmapSafe(rawComet, cometColor, 0.8);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                StarMaskOverlay = starBmp;
                CometMaskOverlay = cometBmp;
                StatusMessage = "Anteprima aggiornata.";
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => StatusMessage = $"Errore anteprima: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task ApplyBatch()
    {
        if (IsProcessingBatch) return;

        IsProcessingBatch = true;
        StatusMessage = "Elaborazione batch...";

        try
        {
            var progress = new Progress<BatchProgressReport>(p => 
            {
                StatusMessage = $"Elaborazione: {p.CurrentFileIndex}/{p.TotalFiles} - {p.CurrentFileName} ({p.Percentage:F0}%)";
            });

            ResultPaths = await _coordinator.ExecuteBatchAsync(_files, _params, progress);
            
            DialogResult = true;
            RequestClose?.Invoke();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Errore batch: {ex.Message}";
        }
        finally
        {
            IsProcessingBatch = false;
        }
    }

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke();

    [RelayCommand]
    private void ResetView() => Viewport.ResetView();

    [RelayCommand]
    private async Task ResetThresholds()
    {
        if (ActiveRenderer != null)
        {
            await ActiveRenderer.ResetThresholdsAsync();
            OnPropertyChanged(nameof(BlackPoint));
            OnPropertyChanged(nameof(WhitePoint));
        }
    }

    private Bitmap? RawMaskToOverlayBitmapSafe(Array rawData, Color overlayColor, double opacity)
    {
        if (rawData is not byte[,] bytes) return null;

        int height = bytes.GetLength(0);
        int width = bytes.GetLength(1);

        var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);

        byte a = (byte)(255 * opacity);
        byte r = overlayColor.R;
        byte g = overlayColor.G;
        byte b = overlayColor.B;
        
        int colorPixel = (a << 24) | (r << 16) | (g << 8) | b;
        int transparentPixel = 0;

        using (var frameBuffer = bitmap.Lock())
        {
            IntPtr backBuffer = frameBuffer.Address;
            int rowBytes = frameBuffer.RowBytes;

            Parallel.For(0, height, y =>
            {
                int[] rowBuffer = ArrayPool<int>.Shared.Rent(width);
                try
                {
                    for (int x = 0; x < width; x++)
                    {
                        rowBuffer[x] = bytes[y, x] > 0 ? colorPixel : transparentPixel;
                    }
                    IntPtr destPtr = IntPtr.Add(backBuffer, y * rowBytes);
                    Marshal.Copy(rowBuffer, 0, destPtr, width);
                }
                finally
                {
                    ArrayPool<int>.Shared.Return(rowBuffer);
                }
            });
        }

        return bitmap;
    }

    public void Dispose()
    {
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        ActiveRenderer?.Dispose();
    }
}