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
using KomaLab.Models.Fits;
using KomaLab.Models.Visualization;
using Point = Avalonia.Point;
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
    [NotifyPropertyChangedFor(nameof(IsNavigationVisible))]
    [NotifyCanExecuteChangedFor(nameof(NextImageCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviousImageCommand))]
    [NotifyCanExecuteChangedFor(nameof(GoToFirstImageCommand))]
    [NotifyCanExecuteChangedFor(nameof(GoToLastImageCommand))]
    private int _currentIndex = 0;

    public string CurrentImageText => $"{CurrentIndex + 1} / {_sourcePaths.Count}";
    public bool IsNavigationVisible => _sourcePaths.Count > 1;

    // --- Parametri di Quantizzazione ---
    [ObservableProperty] 
    private int _levels = 64; 

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

                double minVal, maxVal;
                Cv2.MinMaxLoc(_sourceMat, out minVal, out maxVal);
                SliderMin = minVal;
                SliderMax = maxVal;

                var (currentAutoBlack, currentAutoWhite) = await Task.Run(() => _converter.CalculateDisplayThresholds(data));

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
                        BlackPoint = Math.Clamp(BlackPoint, SliderMin, SliderMax);
                        WhitePoint = Math.Clamp(WhitePoint, SliderMin, SliderMax);
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
        if (_currentData == null) return;
        var (bp, wp) = await Task.Run(() => _converter.CalculateDisplayThresholds(_currentData));
        BlackPoint = Math.Clamp(bp, SliderMin, SliderMax);
        WhitePoint = Math.Clamp(wp, SliderMin, SliderMax);
        if (Math.Abs(WhitePoint - BlackPoint) < 1) WhitePoint = BlackPoint + 100;
        UpdatePreview();
    }

    // --- Aggiornamento Anteprima ---
    partial void OnLevelsChanged(int value) => UpdatePreview();
    partial void OnSelectedModeChanged(VisualizationMode value) => UpdatePreview();
    partial void OnBlackPointChanged(double value)
    {
        // Se il Nero sale e tocca il Bianco, SPINGE il Bianco in su
        if (value >= WhitePoint - 1)
        {
            double pushedWhite = value + 1;
            
            // Se il Bianco sbatte contro il tetto massimo (SliderMax), fermiamo anche il Nero
            if (pushedWhite > SliderMax)
            {
                // Blocca il nero a (Max - 1)
                // Usiamo SetProperty sul campo backing field per evitare loop infiniti se necessario,
                // ma qui basta reimpostare la proprietà corretta.
                if (value > SliderMax - 1)
                {
                    BlackPoint = SliderMax - 1; 
                    return; // Stop qui per evitare doppio update
                }
            }
            else
            {
                // Spingi il bianco
                WhitePoint = pushedWhite;
            }
        }
        
        UpdatePreview();
    }

    partial void OnWhitePointChanged(double value)
    {
        // Se il Bianco scende e tocca il Nero, SPINGE il Nero in giù
        if (value <= BlackPoint + 1)
        {
            double pushedBlack = value - 1;

            // Se il Nero sbatte contro il pavimento (SliderMin), fermiamo anche il Bianco
            if (pushedBlack < SliderMin)
            {
                if (value < SliderMin + 1)
                {
                    WhitePoint = SliderMin + 1;
                    return; // Stop qui
                }
            }
            else
            {
                // Spingi il nero
                BlackPoint = pushedBlack;
            }
        }

        UpdatePreview();
    }

    private void UpdatePreview()
    {
        if (_sourceMat == null || _sourceMat.IsDisposed) return;
        try
        {
            var writeableBmp = new WriteableBitmap(
                new PixelSize(_sourceMat.Width, _sourceMat.Height),
                new Vector(96, 96),
                PixelFormats.Gray8, // Corretto: plurale
                AlphaFormat.Opaque);

            using (var lockedBuffer = writeableBmp.Lock())
            {
                using var dstMat = Mat.FromPixelData(
                    _sourceMat.Height, _sourceMat.Width, MatType.CV_8UC1, 
                    lockedBuffer.Address, lockedBuffer.RowBytes);

                PosterizationService.ComputePosterization(_sourceMat, dstMat, Levels, SelectedMode, BlackPoint, WhitePoint);
            }

            var old = PreviewBitmap; // Risolve warning MVVMTK0034
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

    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    private async Task GoToFirstImage() { CurrentIndex = 0; await LoadImageAtIndexAsync(CurrentIndex); }

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private async Task GoToLastImage() { CurrentIndex = _sourcePaths.Count - 1; await LoadImageAtIndexAsync(CurrentIndex); }

    // --- Applicazione ---
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

            // Calcoliamo l'offset (la "volontà dell'utente") rispetto all'auto-stretch dell'immagine corrente
            double userOffsetBlack = 0;
            double userOffsetWhite = 0;

            if (_autoAdaptThresholds)
            {
                // _lastAutoBlack è il valore calcolato automaticamente per l'immagine che stiamo guardando ORA.
                // BlackPoint è dove l'utente ha spostato lo slider.
                userOffsetBlack = BlackPoint - _lastAutoBlack;
                userOffsetWhite = WhitePoint - _lastAutoWhite;
            }

            for (int i = 0; i < _sourcePaths.Count; i++)
            {
                string path = _sourcePaths[i];
                double targetBlack = BlackPoint;
                double targetWhite = WhitePoint;

                // Se l'adattamento è attivo e NON è l'immagine che stiamo già guardando (per cui le soglie sono già giuste)
                // dobbiamo ricalcolare le soglie specifiche per quel file.
                if (_autoAdaptThresholds && i != _currentIndex)
                {
                    StatusText = $"Calcolo soglie {i + 1}/{_sourcePaths.Count}...";
                    
                    // Nota: Dobbiamo caricare l'header/data per calcolare le statistiche.
                    // È un po' più lento ma garantisce il risultato corretto.
                    var tempData = await _fitsService.LoadFitsFromFileAsync(path);
                    if (tempData != null)
                    {
                        var (autoB, autoW) = await Task.Run(() => _converter.CalculateDisplayThresholds(tempData));
                        
                        // Applichiamo l'offset utente alle statistiche di QUESTA immagine
                        targetBlack = Math.Clamp(autoB + userOffsetBlack, _sliderMin, _sliderMax);
                        targetWhite = Math.Clamp(autoW + userOffsetWhite, _sliderMin, _sliderMax);
                    }
                }

                StatusText = $"Posterizzazione {i + 1}/{_sourcePaths.Count}...";
                
                var outPath = await _postService.PosterizeAndSaveAsync(
                    path, tempFolder, Levels, SelectedMode, targetBlack, targetWhite);
                
                results.Add(outPath);
            }

            ResultPaths = results;
            DialogResult = true;
            RequestClose?.Invoke();
        }
        catch (Exception ex) { StatusText = $"Errore: {ex.Message}"; IsProcessing = false; }
    }

    [RelayCommand] private void Cancel() { DialogResult = false; RequestClose?.Invoke(); }

    public void ZoomIn() => Viewport.ZoomIn();
    public void ZoomOut() => Viewport.ZoomOut();
    public void ResetView() => Viewport.ResetView();
    public void ApplyPan(double dx, double dy) => Viewport.ApplyPan(dx, dy);
    public void ApplyZoomAtPoint(double f, Point c) => Viewport.ApplyZoomAtPoint(f, c);

    public void Dispose() { _sourceMat?.Dispose(); _previewBitmap?.Dispose(); }
}