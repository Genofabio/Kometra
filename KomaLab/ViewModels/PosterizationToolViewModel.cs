using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KomaLab.Models;
using KomaLab.Services.Data;
using KomaLab.Services.Imaging;
using KomaLab.ViewModels.Helpers;
using Avalonia.Media.Imaging;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.IO;
using Avalonia;
using Avalonia.Platform;
using Size = Avalonia.Size;

namespace KomaLab.ViewModels;

public partial class PosterizationToolViewModel : ObservableObject, IDisposable
{
    // --- Dipendenze ---
    private readonly IFitsService _fitsService;
    private readonly IPosterizationService _postService;
    private readonly IFitsDataConverter _converter;
    private readonly IImageAnalysisService _analysis;

    // --- Dati e Stato Interno ---
    private readonly List<string> _sourcePaths;
    private Mat? _sourceMat; 
    private FitsImageData? _currentData;
    
    private double _lastAutoBlack;
    private double _lastAutoWhite;
    private bool _hasLoadedFirstImage = false;

    // --- Output Visuale ---
    [ObservableProperty] private Bitmap? _previewBitmap;
    public ImageViewport Viewport { get; } = new();
    
    // --- Navigazione ---
    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(CurrentImageText))]
    [NotifyCanExecuteChangedFor(nameof(NextImageCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviousImageCommand))]
    private int _currentIndex = 0;

    public string CurrentImageText => $"{CurrentIndex + 1} / {_sourcePaths.Count}";

    // --- Parametri di Quantizzazione ---
    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(LevelsText))] 
    private int _levels = 64; 
    
    public string LevelsText => $"{Levels} Livelli";

    [ObservableProperty] private VisualizationMode _selectedMode;

    // --- Logica Soglie ---
    [ObservableProperty] private bool _autoAdaptThresholds = true; 

    [ObservableProperty] private double _sliderMin = 0;
    [ObservableProperty] private double _sliderMax = 65535;

    [ObservableProperty] private double _blackPoint;
    [ObservableProperty] private double _whitePoint;
    
    [ObservableProperty] private bool _isProcessing;
    [ObservableProperty] private string _statusText = "Pronto";

    // --- Helper UI ---
    public VisualizationMode[] AvailableModes => Enum.GetValues<VisualizationMode>();
    public List<string>? ResultPaths { get; private set; }
    public bool DialogResult { get; private set; }
    public event Action? RequestClose;

    // --- Costruttore ---
    public PosterizationToolViewModel(
        List<string> paths,
        IFitsService fitsService,
        IPosterizationService postService,
        IFitsDataConverter converter,
        IImageAnalysisService analysis,
        VisualizationMode initialMode)
    {
        _sourcePaths = paths;
        _fitsService = fitsService;
        _postService = postService;
        _converter = converter;
        _analysis = analysis;
        _selectedMode = initialMode;

        if (_sourcePaths.Count > 0) 
            _ = LoadImageAtIndexAsync(0);
    }

    // --- Caricamento Immagine e Logica Smart ---
    private async Task LoadImageAtIndexAsync(int index)
    {
        if (index < 0 || index >= _sourcePaths.Count) return;

        StatusText = "Caricamento...";
        try
        {
            _sourceMat?.Dispose(); 
            _sourceMat = null;
            _currentData = null;

            var data = await _fitsService.LoadFitsFromFileAsync(_sourcePaths[index]);
            if (data != null)
            {
                _currentData = data;
                _sourceMat = _converter.RawToMat(data);

                // 1. Limiti Fisici Assoluti
                double minVal, maxVal;
                Cv2.MinMaxLoc(_sourceMat, out minVal, out maxVal);
                SliderMin = minVal;
                SliderMax = maxVal;

                // 2. Statistiche Ottimali Correnti (Auto-Stretch)
                var (currentAutoBlack, currentAutoWhite) = await Task.Run(() => _converter.CalculateDisplayThresholds(data));

                // 3. Applicazione Soglie (Smart vs Fixed)
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
                        // MODO SMART: Mantiene l'offset relativo all'auto-stretch precedente
                        double userOffsetBlack = BlackPoint - _lastAutoBlack;
                        double userOffsetWhite = WhitePoint - _lastAutoWhite;

                        double newBlack = currentAutoBlack + userOffsetBlack;
                        double newWhite = currentAutoWhite + userOffsetWhite;

                        BlackPoint = Math.Clamp(newBlack, SliderMin, SliderMax);
                        WhitePoint = Math.Clamp(newWhite, SliderMin, SliderMax);
                    }
                    else
                    {
                        // MODO FISSO: Mantiene i valori ADU correnti
                        double currentBlack = BlackPoint;
                        double currentWhite = WhitePoint;

                        currentBlack = Math.Clamp(currentBlack, SliderMin, SliderMax);
                        currentWhite = Math.Clamp(currentWhite, SliderMin, SliderMax);

                        if (currentWhite <= currentBlack + 1)
                        {
                            BlackPoint = currentAutoBlack;
                            WhitePoint = currentAutoWhite;
                        }
                        else
                        {
                            BlackPoint = currentBlack;
                            WhitePoint = currentWhite;
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
                OnPropertyChanged(nameof(CurrentImageText));
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Errore: {ex.Message}";
        }
    }

    // --- Reset Soglie ---
    [RelayCommand]
    public async Task ResetThresholdsAsync()
    {
        if (_currentData == null) return;
        var (bp, wp) = await Task.Run(() => _converter.CalculateDisplayThresholds(_currentData));
        BlackPoint = Math.Clamp(bp, SliderMin, SliderMax);
        WhitePoint = Math.Clamp(wp, SliderMin, SliderMax);
        if (Math.Abs(WhitePoint - BlackPoint) < 1) WhitePoint = BlackPoint + 100;
        UpdatePreview();
    }

    // --- Trigger Aggiornamento Anteprima ---
    partial void OnLevelsChanged(int value) => UpdatePreview();
    partial void OnSelectedModeChanged(VisualizationMode value) => UpdatePreview();
    partial void OnBlackPointChanged(double value) { if (value >= WhitePoint) WhitePoint = value + 1; UpdatePreview(); }
    partial void OnWhitePointChanged(double value) { if (value <= BlackPoint) BlackPoint = value - 1; UpdatePreview(); }

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
                    _sourceMat.Height, _sourceMat.Width, MatType.CV_8UC1, 
                    lockedBuffer.Address, lockedBuffer.RowBytes);

                PosterizationService.ComputePosterization(_sourceMat, dstMat, Levels, SelectedMode, BlackPoint, WhitePoint);
            }

            var old = PreviewBitmap;
            PreviewBitmap = writeableBmp;
            old?.Dispose();
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
    }

    // --- Navigazione ---
    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private async Task NextImage() { CurrentIndex++; await LoadImageAtIndexAsync(CurrentIndex); }
    private bool CanGoNext() => CurrentIndex < _sourcePaths.Count - 1;

    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    private async Task PreviousImage() { CurrentIndex--; await LoadImageAtIndexAsync(CurrentIndex); }
    private bool CanGoPrevious() => CurrentIndex > 0;

    // --- Applicazione Finale ---
    [RelayCommand]
    private async Task Apply()
    {
        if (IsProcessing) return;
        IsProcessing = true;
        StatusText = "Elaborazione...";
        try
        {
            var results = new List<string>();
            string tempFolder = Path.Combine(Path.GetTempPath(), "KomaLab", "Posterized");
            if (!Directory.Exists(tempFolder)) Directory.CreateDirectory(tempFolder);

            foreach (var path in _sourcePaths)
            {
                var outPath = await _postService.PosterizeAndSaveAsync(path, tempFolder, Levels, SelectedMode, BlackPoint, WhitePoint);
                results.Add(outPath);
            }
            ResultPaths = results;
            DialogResult = true;
            RequestClose?.Invoke();
        }
        catch (Exception ex) { StatusText = $"Errore: {ex.Message}"; IsProcessing = false; }
    }

    [RelayCommand] private void Cancel() { DialogResult = false; RequestClose?.Invoke(); }

    // --- Proxy Viewport ---
    public void ZoomIn() => Viewport.ZoomIn();
    public void ZoomOut() => Viewport.ZoomOut();
    public void ResetView() => Viewport.ResetView();
    public void ApplyPan(double dx, double dy) => Viewport.ApplyPan(dx, dy);
    public void ApplyZoomAtPoint(double f, Avalonia.Point c) => Viewport.ApplyZoomAtPoint(f, c);

    public void Dispose() { _sourceMat?.Dispose(); _previewBitmap?.Dispose(); }
}