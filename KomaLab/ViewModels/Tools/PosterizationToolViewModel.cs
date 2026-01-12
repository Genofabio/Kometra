using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KomaLab.Models.Visualization;
using KomaLab.Services.Data;
using KomaLab.Services.Imaging;
using KomaLab.ViewModels.Visualization;
using OpenCvSharp;

using Point = Avalonia.Point;
using Size = Avalonia.Size;

namespace KomaLab.ViewModels.Tools;

// ---------------------------------------------------------------------------
// FILE: PosterizationToolViewModel.cs
// RUOLO: ViewModel Tool Interattivo
// DESCRIZIONE:
// Gestisce l'interfaccia di anteprima per il tool di posterizzazione.
// Responsabilità:
// 1. Caricamento e gestione della Matrice OpenCV sorgente.
// 2. Rendering Real-Time dell'anteprima (Preview) richiamando il servizio.
// 3. Applicazione batch delle impostazioni delegando la logica al PosterizationService.
// ---------------------------------------------------------------------------

public partial class PosterizationToolViewModel : ObservableObject, IDisposable
{
    // --- Dipendenze ---
    private readonly IFitsIoService _ioService;
    private readonly IPosterizationService _postService;
    private readonly IFitsImageDataConverter _converter;
    private readonly IImageAnalysisService _analysis;

    // --- Stato Interno ---
    private readonly List<string> _sourcePaths;
    private Mat? _sourceMat; 
    private double _lastAutoBlack;
    private double _lastAutoWhite;
    private bool _hasLoadedFirstImage = false;

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
        IFitsImageDataConverter converter,
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

    // --- Logica Caricamento ---

    private async Task LoadImageAtIndexAsync(int index)
    {
        if (index < 0 || index >= _sourcePaths.Count) return;

        StatusText = "Caricamento...";
        try
        {
            _sourceMat?.Dispose(); 
            _sourceMat = null;

            var data = await _ioService.LoadAsync(_sourcePaths[index]);
            if (data != null)
            {
                _sourceMat = _converter.RawToMat(data);

                Cv2.MinMaxLoc(_sourceMat, out double minVal, out double maxVal);
                SliderMin = minVal;
                SliderMax = maxVal;

                var (currentAutoBlack, currentAutoWhite) = await Task.Run(() => _analysis.CalculateAutoStretchLevels(_sourceMat));

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
                        double userOffsetBlack = BlackPoint - _lastAutoBlack;
                        double userOffsetWhite = WhitePoint - _lastAutoWhite;
                        BlackPoint = Math.Clamp(currentAutoBlack + userOffsetBlack, SliderMin, SliderMax);
                        WhitePoint = Math.Clamp(currentAutoWhite + userOffsetWhite, SliderMin, SliderMax);
                    }
                    else
                    {
                        BlackPoint = Math.Clamp((double)BlackPoint, SliderMin, SliderMax);
                        WhitePoint = Math.Clamp((double)WhitePoint, SliderMin, SliderMax);
                        if (WhitePoint <= BlackPoint + 1)
                        {
                            BlackPoint = currentAutoBlack;
                            WhitePoint = currentAutoWhite;
                        }
                    }
                }

                _lastAutoBlack = currentAutoBlack;
                _lastAutoWhite = currentAutoWhite;

                if (Viewport.ImageSize.Width != data.Width || Viewport.ImageSize.Height != data.Height)
                {
                    Viewport.ImageSize = new Size(data.Width, data.Height);
                    Viewport.ResetView();
                }

                UpdatePreview();
                StatusText = "Pronto";
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
        
        var (autoB, autoW) = await Task.Run(() => _analysis.CalculateAutoStretchLevels(_sourceMat));
        
        BlackPoint = Math.Clamp(autoB, SliderMin, SliderMax);
        WhitePoint = Math.Clamp(autoW, SliderMin, SliderMax);
        
        if (Math.Abs((double)(WhitePoint - BlackPoint)) < 1) WhitePoint = BlackPoint + 100;
        
        UpdatePreview();
    }

    // --- Gestione Cambiamenti UI ---
    
    partial void OnLevelsChanged(int value) => UpdatePreview();
    partial void OnSelectedModeChanged(VisualizationMode value) => UpdatePreview();
    
    partial void OnBlackPointChanged(double value)
    {
        if (value >= WhitePoint - 1)
        {
            double pushedWhite = value + 1;
            if (pushedWhite > SliderMax)
            {
                if (value > SliderMax - 1) BlackPoint = SliderMax - 1; 
            }
            else WhitePoint = pushedWhite;
        }
        UpdatePreview();
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
        UpdatePreview();
    }

    // --- Core Rendering Anteprima ---

    private void UpdatePreview()
    {
        if (_sourceMat == null || _sourceMat.IsDisposed) return;
        
        try
        {
            var writeableBmp = new WriteableBitmap(
                new PixelSize(_sourceMat.Width, _sourceMat.Height),
                new Vector(96, 96),
                PixelFormats.Gray8, 
                AlphaFormat.Opaque);

            using (var lockedBuffer = writeableBmp.Lock())
            {
                using var dstMat = Mat.FromPixelData(
                    _sourceMat.Height, 
                    _sourceMat.Width, 
                    MatType.CV_8UC1, 
                    lockedBuffer.Address, 
                    lockedBuffer.RowBytes);

                // LOGICA MIGLIORATA: Chiamata al metodo statico del servizio (DRY)
                PosterizationService.ComputePosterization(_sourceMat, dstMat, Levels, SelectedMode, BlackPoint, WhitePoint);
            }

            var old = PreviewBitmap; 
            PreviewBitmap = writeableBmp;
            old?.Dispose();
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Preview Error: {ex.Message}"); }
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

            // LOGICA MIGLIORATA: Calcoliamo gli offset e deleghiamo il batch al servizio
            double offsetBlack = AutoAdaptThresholds ? (BlackPoint - _lastAutoBlack) : 0;
            double offsetWhite = AutoAdaptThresholds ? (WhitePoint - _lastAutoWhite) : 0;

            if (AutoAdaptThresholds)
            {
                // Il servizio gestisce internamente il loop, l'analisi e l'applicazione degli offset
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
                // Se non è adattivo, usiamo i punti fissi per tutti in parallelo
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
        _sourceMat?.Dispose(); 
        _previewBitmap?.Dispose(); 
        GC.SuppressFinalize(this);
    }
}