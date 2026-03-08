using System;
using System.Buffers; 
using System.Collections.Generic;
using System.Linq;
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
using Kometra.Infrastructure; // Aggiunto per localizzazione
using Kometra.Services.Processing.Batch;
using Kometra.Models.Fits;
using Kometra.Models.Fits.Structure;
using Kometra.Models.Processing;
using Kometra.Models.Processing.Batch;
using Kometra.Models.Processing.Masking;
using Kometra.Services.Factories;
using Kometra.Services.Fits;
using Kometra.Services.Processing.Coordinators;
using Kometra.ViewModels.Visualization;
using SequenceNavigator = Kometra.ViewModels.Shared.SequenceNavigator;

namespace Kometra.ViewModels.ImageProcessing;

public enum StarRemovalState
{
    Setup,          
    Calculating,    
    ResultsReady,   
    ProcessingBatch 
}

public partial class StarMaskingViewModel : ObservableObject, IDisposable
{
    private readonly IMaskingCoordinator _coordinator;
    private readonly IFitsDataManager _dataManager;
    private readonly IFitsRendererFactory _rendererFactory;
    
    private readonly List<FitsFileReference> _files;
    private CancellationTokenSource? _cts;           
    private CancellationTokenSource? _maskPreviewCts;
    private bool _isDisposed; // Flag per evitare operazioni se già chiuso

    public event Action? RequestFitToScreen;
    public event Action? RequestClose;

    public SequenceNavigator Navigator { get; } = new();
    public AlignmentImageViewport Viewport { get; } = new();

    private readonly MaskingParameters _params = new();

    #region Observable Properties

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInteractionEnabled))]
    [NotifyPropertyChangedFor(nameof(IsSetupVisible))]
    [NotifyPropertyChangedFor(nameof(IsResultActionsVisible))]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    private StarRemovalState _currentState = StarRemovalState.Setup;

    [ObservableProperty] private FitsRenderer? _activeRenderer;
    [ObservableProperty] private string _statusMessage = string.Empty;

    [ObservableProperty] private Bitmap? _starMaskOverlay;
    [ObservableProperty] private Bitmap? _cometMaskOverlay;
    
    [ObservableProperty] private bool _showStarMask = true;
    [ObservableProperty] private bool _showCometMask = true;
    
    // --- Parametri con Trigger ---
    
    public double CometThreshold
    {
        get => _params.CometThresholdSigma;
        set { if (SetProperty(_params.CometThresholdSigma, value, _params, (u, n) => u.CometThresholdSigma = n)) TriggerMaskPreview(); }
    }
    public int CometDilation
    {
        get => _params.CometDilation;
        set { if (SetProperty(_params.CometDilation, value, _params, (u, n) => u.CometDilation = n)) TriggerMaskPreview(); }
    }

    public double StarThreshold
    {
        get => _params.StarThresholdSigma;
        set { if (SetProperty(_params.StarThresholdSigma, value, _params, (u, n) => u.StarThresholdSigma = n)) TriggerMaskPreview(); }
    }
    public int StarDilation
    {
        get => _params.StarDilation;
        set { if (SetProperty(_params.StarDilation, value, _params, (u, n) => u.StarDilation = n)) TriggerMaskPreview(); }
    }

    public int MinStarDiameter
    {
        get => _params.MinStarDiameter;
        set { if (SetProperty(_params.MinStarDiameter, value, _params, (u, n) => u.MinStarDiameter = n)) TriggerMaskPreview(); }
    }

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

    public bool IsInteractionEnabled => !IsBusy;
    public bool IsSetupVisible => CurrentState == StarRemovalState.Setup;
    public bool IsResultActionsVisible => CurrentState == StarRemovalState.ResultsReady;
    public bool IsBusy => _currentState == StarRemovalState.Calculating || _currentState == StarRemovalState.ProcessingBatch;
    
    public string CurrentImageText => $"{Navigator.DisplayIndex} / {Navigator.TotalCount}";

    public List<string>? ResultPaths { get; private set; }
    public bool DialogResult { get; private set; }

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

        _statusMessage = LocalizationManager.Instance["StarStatusReady"];

        Navigator.UpdateStatus(0, _files.Count);
        Navigator.IndexChanged += async (s, i) => await LoadImageAsync(i, autoFit: false);

        _ = LoadImageAsync(0, autoFit: true);
    }

    private async Task LoadImageAsync(int index, bool autoFit)
    {
        if (_isDisposed || index < 0 || index >= _files.Count) return;
        
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        try
        {
            var file = _files[index];

            if (CurrentState == StarRemovalState.ResultsReady)
            {
                StarMaskOverlay = null;
                CometMaskOverlay = null;
                StatusMessage = LocalizationManager.Instance["StarStatusCalculating"];
                
                var starlessPixels = await _coordinator.ProcessPreviewAsync(file, _params, token);
                
                var originalData = await _dataManager.GetDataAsync(file.FilePath);
                var originalHeader = (originalData.FirstImageHdu ?? originalData.PrimaryHdu)?.Header;

                await UpdateRendererAsync(starlessPixels, originalHeader, autoFit);
                StatusMessage = LocalizationManager.Instance["StarStatusResultReady"];
            }
            else
            {
                StatusMessage = LocalizationManager.Instance["StarStatusLoading"];
                var data = await _dataManager.GetDataAsync(file.FilePath);
                var hdu = data.FirstImageHdu ?? data.PrimaryHdu;
                if (hdu == null) throw new InvalidOperationException(LocalizationManager.Instance["StarErrorInvalidImage"]);

                await UpdateRendererAsync(hdu.PixelData, hdu.Header, autoFit);
                
                TriggerMaskPreview();
                StatusMessage = LocalizationManager.Instance["StarStatusReady"];
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_isDisposed)
            {
                StatusMessage = string.Format(LocalizationManager.Instance["EnhanceStatusError"], ex.Message);
                if (CurrentState == StarRemovalState.ResultsReady) CurrentState = StarRemovalState.Setup;
            }
        }
    }

    private async void TriggerMaskPreview()
    {
        if (_isDisposed || ActiveRenderer == null || CurrentState != StarRemovalState.Setup) return;

        _maskPreviewCts?.Cancel();
        _maskPreviewCts = new CancellationTokenSource();
        var token = _maskPreviewCts.Token;

        try
        {
            await Task.Delay(150, token);

            var currentFile = _files[Navigator.CurrentIndex];
            var (rawStar, rawComet) = await _coordinator.CalculateMasksPreviewAsync(currentFile, _params, token);

            if (token.IsCancellationRequested) return;

            var starColor = Color.Parse("#567FFF"); 
            var cometColor = Color.Parse("#FFEA7B");

            var starBmp = RawMaskToOverlayBitmapSafe(rawStar, starColor, 0.5);
            var cometBmp = RawMaskToOverlayBitmapSafe(rawComet, cometColor, 0.5);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!_isDisposed)
                {
                    StarMaskOverlay = starBmp;
                    CometMaskOverlay = cometBmp;
                }
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception) { }
    }

    private async Task UpdateRendererAsync(Array pixels, FitsHeader? header, bool autoFit)
    {
        if (_isDisposed) return;

        var newRenderer = await _rendererFactory.CreateAsync(pixels, header);

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

        if (autoFit) RequestFitToScreen?.Invoke();
    }

    [RelayCommand]
    private async Task Calculate()
    {
        if (IsBusy) return;
        
        CurrentState = StarRemovalState.Calculating;
        CurrentState = StarRemovalState.ResultsReady; 
        
        await LoadImageAsync(Navigator.CurrentIndex, autoFit: false);
    }

    [RelayCommand]
    private async Task Cancel()
    {
        CurrentState = StarRemovalState.Setup;
        await LoadImageAsync(Navigator.CurrentIndex, autoFit: false);
    }

    [RelayCommand]
    private async Task ApplyBatch()
    {
        CurrentState = StarRemovalState.ProcessingBatch;
        StatusMessage = LocalizationManager.Instance["StarStatusBatchInit"];
        
        _cts?.Cancel();
        _cts = new CancellationTokenSource();

        try
        {
            var progress = new Progress<BatchProgressReport>(p => 
                StatusMessage = string.Format(LocalizationManager.Instance["StarStatusBatchProgress"], p.CurrentFileIndex, p.TotalFiles, p.Percentage));

            // Questo metodo è sequenziale e attende la fine di tutto
            ResultPaths = await _coordinator.ExecuteBatchAsync(_files, _params, progress, _cts.Token);
            
            // Se arriviamo qui, il batch è finito con successo
            DialogResult = true;
            RequestClose?.Invoke();
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(LocalizationManager.Instance["StarErrorProcessing"], ex.Message);
            CurrentState = StarRemovalState.ResultsReady;
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

    [RelayCommand] private void ResetView() => Viewport.ResetView();
    
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

    // ==========================================================
    // DISPOSE SICURO (Fix per Double Dispose Crash)
    // ==========================================================
    public void Dispose()
    {
        if (_isDisposed) return; // Se già chiuso, esci subito
        _isDisposed = true;

        // Pulizia CTS Principale
        if (_cts != null)
        {
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
            _cts.Dispose();
            _cts = null;
        }

        // Pulizia CTS Preview
        if (_maskPreviewCts != null)
        {
            try { _maskPreviewCts.Cancel(); } catch (ObjectDisposedException) { }
            _maskPreviewCts.Dispose();
            _maskPreviewCts = null;
        }

        // Pulizia Risorse Grafiche
        ActiveRenderer?.Dispose();
        ActiveRenderer = null;
    }
}