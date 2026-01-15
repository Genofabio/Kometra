using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KomaLab.Models.Fits; // Per AbsoluteContrastProfile
using KomaLab.Models.Visualization;
using KomaLab.Services.Fits;
using KomaLab.Services.Processing;
using KomaLab.ViewModels.Visualization;
using OpenCvSharp;

using Point = Avalonia.Point;
using Size = Avalonia.Size;

namespace KomaLab.ViewModels.Tools;

// ---------------------------------------------------------------------------
// FILE: PosterizationToolViewModel.cs
// RUOLO: ViewModel Tool Interattivo
// VERSIONE: Aggiornata per Architettura No-FitsImageData
// ---------------------------------------------------------------------------

public partial class PosterizationToolViewModel : ObservableObject, IDisposable
{
    // --- Dipendenze ---
    private readonly IFitsIoService _ioService;
    private readonly IPosterizationService _postService;
    private readonly IFitsOpenCvConverter _converter;
    private readonly IImageAnalysisService _analysis;

    // --- Stato Interno ---
    private readonly List<string> _sourcePaths;
    private Mat? _sourceMat; 
    private double _lastAutoBlack;
    private double _lastAutoWhite;
    private bool _hasLoadedFirstImage = false;
    
    // BACK BUFFER per riciclo memoria (ottimizzazione rendering)
    private WriteableBitmap? _backBuffer;
    private CancellationTokenSource? _previewCts;

    // --- Output Visuale ---
    [ObservableProperty] private Bitmap? _previewBitmap;
    public ImageViewport Viewport { get; } = new();
    
    // --- Navigazione ---
    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(CurrentImageText))]
    [NotifyPropertyChangedFor(nameof(IsNavigationVisible))]
    [NotifyCanExecuteChangedFor(nameof(NextImageCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviousImageCommand))]
    [NotifyCanExecuteChangedFor(nameof(GoToFirstImageCommand))]
    [NotifyCanExecuteChangedFor(nameof(GoToLastImageCommand))]
    private int _currentIndex = 0;

    public string CurrentImageText => $"{CurrentIndex + 1} / {_sourcePaths.Count}";
    public bool IsNavigationVisible => _sourcePaths.Count > 1;

    // --- Parametri UI ---
    [ObservableProperty] private int _levels = 64; 
    [ObservableProperty] private VisualizationMode _selectedMode;

    [ObservableProperty] private bool _autoAdaptThresholds = true; 
    [ObservableProperty] private double _sliderMin = 0;
    [ObservableProperty] private double _sliderMax = 65535;

    [ObservableProperty] private double _blackPoint;
    [ObservableProperty] private double _whitePoint;
    
    [ObservableProperty] private bool _isProcessing;
    [ObservableProperty] private string _statusText = "Pronto";

    // --- Output del Dialogo ---
    public VisualizationMode[] AvailableModes => Enum.GetValues<VisualizationMode>();
    public List<string>? ResultPaths { get; private set; }
    public bool DialogResult { get; private set; }
    public event Action? RequestClose;

    // --- Costruttore ---
    public PosterizationToolViewModel(
        List<string> paths,
        IFitsIoService ioService,
        IPosterizationService postService,
        IFitsOpenCvConverter converter,
        IImageAnalysisService analysis,
        VisualizationMode initialMode)
    {
        _sourcePaths = paths ?? new List<string>();
        _ioService = ioService ?? throw new ArgumentNullException(nameof(ioService));
        _postService = postService ?? throw new ArgumentNullException(nameof(postService));
        _converter = converter ?? throw new ArgumentNullException(nameof(converter));
        _analysis = analysis ?? throw new ArgumentNullException(nameof(analysis));
        
        _selectedMode = initialMode;

        if (_sourcePaths.Count > 0) 
            _ = LoadImageAtIndexAsync(0);
    }

    // --- Logica Caricamento (AGGIORNATA) ---

    private async Task LoadImageAtIndexAsync(int index)
    {
        if (index < 0 || index >= _sourcePaths.Count) return;

        StatusText = "Caricamento...";
        try
        {
            // Reset risorsa corrente
            if (_sourceMat != null && !_sourceMat.IsDisposed) 
            {
                _sourceMat.Dispose();
                _sourceMat = null;
            }

            string path = _sourcePaths[index];

            // 1. Caricamento Separato (Header + Pixel)
            var header = await _ioService.ReadHeaderAsync(path);
            var pixels = await _ioService.ReadPixelDataAsync(path);

            if (header != null && pixels != null)
            {
                // 2. Estrazione parametri scala
                double bScale = header.GetValue<double>("BSCALE") ?? 1.0;
                double bZero = header.GetValue<double>("BZERO") ?? 0.0;

                // 3. Conversione in Matrice
                _sourceMat = _converter.RawToMat(pixels, bScale, bZero);

                // 4. Analisi Statistica per Slider
                Cv2.MinMaxLoc(_sourceMat, out double minVal, out double maxVal);
                SliderMin = minVal;
                SliderMax = maxVal;

                // 5. AutoStretch
                var profile = await Task.Run(() => _analysis.CalculateAutoStretchProfile(_sourceMat));
                double currentAutoBlack = profile.BlackAdu;
                double currentAutoWhite = profile.WhiteAdu;

                // 6. Logica Adattiva Intelligente (Preserva decisioni utente)
                if (!_hasLoadedFirstImage)
                {
                    BlackPoint = currentAutoBlack;
                    WhitePoint = currentAutoWhite;
                    _hasLoadedFirstImage = true;
                }
                else
                {
                    if (AutoAdaptThresholds)
                    {
                        // Mantiene l'offset relativo impostato dall'utente
                        double userOffsetBlack = BlackPoint - _lastAutoBlack;
                        double userOffsetWhite = WhitePoint - _lastAutoWhite;
                        BlackPoint = Math.Clamp(currentAutoBlack + userOffsetBlack, SliderMin, SliderMax);
                        WhitePoint = Math.Clamp(currentAutoWhite + userOffsetWhite, SliderMin, SliderMax);
                    }
                    else
                    {
                        // Clamp base
                        BlackPoint = Math.Clamp(BlackPoint, SliderMin, SliderMax);
                        WhitePoint = Math.Clamp(WhitePoint, SliderMin, SliderMax);
                        
                        // Reset se valori incoerenti
                        if (WhitePoint <= BlackPoint + 1)
                        {
                            BlackPoint = currentAutoBlack;
                            WhitePoint = currentAutoWhite;
                        }
                    }
                }

                _lastAutoBlack = currentAutoBlack;
                _lastAutoWhite = currentAutoWhite;

                // Aggiornamento Viewport se dimensioni cambiate
                if (Viewport.ImageSize.Width != _sourceMat.Width || Viewport.ImageSize.Height != _sourceMat.Height)
                {
                    Viewport.ImageSize = new Size(_sourceMat.Width, _sourceMat.Height);
                    Viewport.ResetView();
                }

                TriggerPreviewUpdate();
                StatusText = "Pronto";
            }
            else
            {
                StatusText = "Errore: Impossibile leggere il file FITS.";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Errore: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task ResetThresholdsAsync()
    {
        if (_sourceMat == null) return;
        
        var profile = await Task.Run(() => _analysis.CalculateAutoStretchProfile(_sourceMat));
        
        BlackPoint = Math.Clamp(profile.BlackAdu, SliderMin, SliderMax);
        WhitePoint = Math.Clamp(profile.WhiteAdu, SliderMin, SliderMax);
        
        if (Math.Abs((double)(WhitePoint - BlackPoint)) < 1) WhitePoint = BlackPoint + 100;
        
        TriggerPreviewUpdate();
    }

    // --- Gestione Cambiamenti UI ---
    
    partial void OnLevelsChanged(int value) => TriggerPreviewUpdate();
    partial void OnSelectedModeChanged(VisualizationMode value) => TriggerPreviewUpdate();
    
    partial void OnBlackPointChanged(double value)
    {
        // Logica "Push" per evitare inversione slider
        if (value >= WhitePoint - 1)
        {
            double pushedWhite = value + 1;
            if (pushedWhite > SliderMax)
            {
                if (value > SliderMax - 1) BlackPoint = SliderMax - 1; 
            }
            else WhitePoint = pushedWhite;
        }
        TriggerPreviewUpdate();
    }

    partial void OnWhitePointChanged(double value)
    {
        if (value <= BlackPoint + 1)
        {
            double pushedBlack = value - 1;
            if (pushedBlack < SliderMin)
            {
                if (value < SliderMin + 1) WhitePoint = SliderMin + 1;
            }
            else BlackPoint = pushedBlack;
        }
        TriggerPreviewUpdate();
    }

    // --- Core Rendering Anteprima (Double Buffered) ---

    private void TriggerPreviewUpdate()
    {
        _previewCts?.Cancel();
        _previewCts = new CancellationTokenSource();
        _ = UpdatePreviewAsync(_previewCts.Token);
    }

    private async Task UpdatePreviewAsync(CancellationToken token)
    {
        if (_sourceMat == null || _sourceMat.IsDisposed) return;

        // 1. Allocazione / Recupero Back Buffer
        WriteableBitmap targetBmp;
        bool createdNew = false;

        if (_backBuffer != null && 
            _backBuffer.PixelSize.Width == _sourceMat.Width && 
            _backBuffer.PixelSize.Height == _sourceMat.Height)
        {
            targetBmp = _backBuffer;
            _backBuffer = null;
        }
        else
        {
            targetBmp = new WriteableBitmap(
                new PixelSize(_sourceMat.Width, _sourceMat.Height),
                new Vector(96, 96),
                PixelFormats.Gray8, 
                AlphaFormat.Opaque);
            createdNew = true;
        }

        try
        {
            // 2. Rendering sul thread pool (non blocca UI)
            await Task.Run(() => 
            {
                token.ThrowIfCancellationRequested();
                using var locked = targetBmp.Lock();
                
                // Creiamo un Mat Wrapper attorno al buffer della bitmap (Zero Copy scrittura)
                using var dstMat = Mat.FromPixelData(
                    _sourceMat.Height, _sourceMat.Width, MatType.CV_8UC1, locked.Address, locked.RowBytes);
                
                // Chiamata al servizio
                _postService.ComputePosterizationOnMat(_sourceMat, dstMat, Levels, SelectedMode, BlackPoint, WhitePoint);
            }, token);

            // 3. Swap (Thread UI)
            SwapPreview(targetBmp);
        }
        catch (OperationCanceledException)
        {
            // Se cancellato, rimettiamo il buffer in scorta
            if (!createdNew) _backBuffer = targetBmp;
            else targetBmp.Dispose();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Preview Error: {ex.Message}");
            targetBmp.Dispose();
        }
    }

    private void SwapPreview(Bitmap newBmp)
    {
        var old = PreviewBitmap as WriteableBitmap;
        PreviewBitmap = newBmp;
        
        // Riciclo buffer vecchio
        if (old != null)
        {
            if (_backBuffer == null) _backBuffer = old;
            else old.Dispose();
        }
    }

    // --- Comandi Navigazione ---
    
    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private async Task NextImage() { CurrentIndex++; await LoadImageAtIndexAsync(CurrentIndex); }
    private bool CanGoNext() => CurrentIndex < _sourcePaths.Count - 1;

    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    private async Task PreviousImage() { CurrentIndex--; await LoadImageAtIndexAsync(CurrentIndex); }
    private bool CanGoPrevious() => CurrentIndex > 0;

    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    private async Task GoToFirstImage() { CurrentIndex = 0; await LoadImageAtIndexAsync(CurrentIndex); }

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private async Task GoToLastImage() { CurrentIndex = _sourcePaths.Count - 1; await LoadImageAtIndexAsync(CurrentIndex); }

    // --- Applicazione Finale ---

    [RelayCommand]
    private async Task Apply()
    {
        if (IsProcessing) return;
        IsProcessing = true;
        StatusText = "Elaborazione in corso...";
        
        try
        {
            string tempFolder = Path.Combine(Path.GetTempPath(), "KomaLab", "Posterized");
            if (!Directory.Exists(tempFolder)) Directory.CreateDirectory(tempFolder);

            double offsetBlack = AutoAdaptThresholds ? (BlackPoint - _lastAutoBlack) : 0;
            double offsetWhite = AutoAdaptThresholds ? (WhitePoint - _lastAutoWhite) : 0;

            // Delega al servizio (già aggiornato per gestire I/O internamente)
            if (AutoAdaptThresholds)
            {
                ResultPaths = await _postService.PosterizeBatchWithOffsetsAsync(
                    _sourcePaths, 
                    tempFolder, 
                    Levels, 
                    SelectedMode, 
                    offsetBlack, 
                    offsetWhite);
            }
            else
            {
                // Fallback loop manuale (se necessario)
                var tasks = new List<Task<string>>();
                foreach (var path in _sourcePaths)
                {
                    tasks.Add(_postService.PosterizeAndSaveAsync(
                        path, 
                        tempFolder, 
                        Levels, 
                        SelectedMode, 
                        BlackPoint, 
                        WhitePoint));
                }
                
                var results = await Task.WhenAll(tasks);
                ResultPaths = new List<string>(results);
            }

            DialogResult = true;
            RequestClose?.Invoke();
        }
        catch (Exception ex) 
        { 
            StatusText = $"Errore Critico: {ex.Message}"; 
            IsProcessing = false; 
        }
    }

    [RelayCommand] 
    private void Cancel() 
    { 
        DialogResult = false; 
        RequestClose?.Invoke(); 
    }

    // --- Integrazione Viewport (Zoom e Pan) ---

    public void ZoomIn() => Viewport.ZoomIn();
    public void ZoomOut() => Viewport.ZoomOut();
    public void ResetView() => Viewport.ResetView();
    public void ApplyPan(double dx, double dy) => Viewport.ApplyPan(dx, dy);
    public void ApplyZoomAtPoint(double f, Point c) => Viewport.ApplyZoomAtPoint(f, c);

    public void Dispose() 
    { 
        _previewCts?.Cancel();
        _sourceMat?.Dispose(); 
        
        (PreviewBitmap as IDisposable)?.Dispose();
        _backBuffer?.Dispose();
        
        GC.SuppressFinalize(this);
    }
}