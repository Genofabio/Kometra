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
// 2. Rendering Real-Time dell'anteprima (Preview) quando l'utente muove gli slider.
// 3. Applicazione batch delle impostazioni a tutte le immagini selezionate.
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
                // *** FIX APPLICATO QUI ***
                // Invece di 'new Mat(...)', usiamo 'Mat.FromPixelData'
                using var dstMat = Mat.FromPixelData(
                    _sourceMat.Height, 
                    _sourceMat.Width, 
                    MatType.CV_8UC1, 
                    lockedBuffer.Address, 
                    lockedBuffer.RowBytes);

                ComputePreviewPosterization(_sourceMat, dstMat, Levels, SelectedMode, BlackPoint, WhitePoint);
            }

            var old = PreviewBitmap; 
            PreviewBitmap = writeableBmp;
            old?.Dispose();
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Preview Error: {ex.Message}"); }
    }

    private static void ComputePreviewPosterization(Mat src, Mat dst, int levels, VisualizationMode mode, double bp, double wp)
    {
        double range = wp - bp;
        if (Math.Abs(range) < 1e-9) range = 1e-5;
        
        using var temp32f = new Mat();
        
        double scale = 1.0 / range;
        double offset = -bp * scale;
        src.ConvertTo(temp32f, MatType.CV_32FC1, scale, offset);
        
        Cv2.Max(temp32f, 0.0, temp32f);
        Cv2.Min(temp32f, 1.0, temp32f);

        if (mode == VisualizationMode.SquareRoot)
        {
            Cv2.Sqrt(temp32f, temp32f);
        }
        else if (mode == VisualizationMode.Logarithmic)
        {
            Cv2.Add(temp32f, 1.0, temp32f);
            Cv2.Log(temp32f, temp32f);
            Cv2.Multiply(temp32f, 1.442695, temp32f);
        }

        Cv2.Multiply(temp32f, (double)levels - 0.001, temp32f);
        
        using var temp32s = new Mat();
        temp32f.ConvertTo(temp32s, MatType.CV_32SC1);
        temp32s.ConvertTo(temp32f, MatType.CV_32FC1); 
        
        double divScale = levels > 1 ? 1.0 / (levels - 1) : 1.0;
        Cv2.Multiply(temp32f, divScale, temp32f);

        temp32f.ConvertTo(dst, MatType.CV_8UC1, 255.0, 0);
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
            var results = new List<string>();
            string tempFolder = Path.Combine(Path.GetTempPath(), "KomaLab", "Posterized");
            if (!Directory.Exists(tempFolder)) Directory.CreateDirectory(tempFolder);

            double userOffsetBlack = 0;
            double userOffsetWhite = 0;

            if (_autoAdaptThresholds)
            {
                userOffsetBlack = BlackPoint - _lastAutoBlack;
                userOffsetWhite = WhitePoint - _lastAutoWhite;
            }

            for (int i = 0; i < _sourcePaths.Count; i++)
            {
                string path = _sourcePaths[i];
                double targetBlack = BlackPoint;
                double targetWhite = WhitePoint;

                if (_autoAdaptThresholds && i != _currentIndex)
                {
                    StatusText = $"Analisi {i + 1}/{_sourcePaths.Count}...";
                    
                    var tempData = await _ioService.LoadAsync(path);
                    if (tempData != null)
                    {
                        using var tempMat = _converter.RawToMat(tempData);
                        var (autoB, autoW) = await Task.Run(() => _analysis.CalculateAutoStretchLevels(tempMat));
                        
                        targetBlack = Math.Clamp(autoB + userOffsetBlack, _sliderMin, _sliderMax);
                        targetWhite = Math.Clamp(autoW + userOffsetWhite, _sliderMin, _sliderMax);
                    }
                }

                StatusText = $"Rendering {i + 1}/{_sourcePaths.Count}...";
                
                var outPath = await _postService.PosterizeAndSaveAsync(
                    path, tempFolder, Levels, SelectedMode, targetBlack, targetWhite);
                
                results.Add(outPath);
            }

            ResultPaths = results;
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