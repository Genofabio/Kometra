using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KomaLab.Models.Fits;
using KomaLab.Models.Nodes;
using KomaLab.Models.Visualization;
using KomaLab.Services.Factories;
using KomaLab.Services.Fits;
using KomaLab.Services.Processing;
using KomaLab.ViewModels.Visualization;

namespace KomaLab.ViewModels.Nodes;

public partial class MultipleImagesNodeViewModel : ImageNodeViewModel
{
    private const int AnimationIntervalMs = 250;

    private readonly IFitsIoService _ioService;
    private readonly IFitsOpenCvConverter _converter;
    private readonly IImageAnalysisService _analysis;
    private readonly IFitsRendererFactory _rendererFactory;
    private readonly MultipleImagesNodeModel _multiModel;

    // --- Stato Dati ---
    private FitsCollection _collection; 
    private readonly DispatcherTimer _animationTimer;
    private CancellationTokenSource? _loadingCts;
    private Size _maxImageSize;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveRenderer))]
    private FitsRenderer? _activeFitsImage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanShowPrevious), nameof(CanShowNext))]
    [NotifyCanExecuteChangedFor(nameof(PreviousImageCommand), nameof(NextImageCommand))]
    private bool _isAnimating;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentImageText), nameof(CanShowPrevious), nameof(CanShowNext), nameof(ActiveFile))]
    private int _currentIndex;

    // --- Implementazione ImageNodeViewModel ---
    public override FitsRenderer? ActiveRenderer => ActiveFitsImage;
    public override Size NodeContentSize => _maxImageSize;

    // Contratti Base
    public override FitsCollection? OutputCollection => _collection;
    public override FitsFileReference? ActiveFile => (_collection != null && _currentIndex >= 0 && _currentIndex < _collection.Count)
        ? _collection[_currentIndex]
        : null;

    protected override void OnRendererSwapping(FitsRenderer newRenderer) => ActiveFitsImage = newRenderer;

    // --- Proprietà UI e Compatibilità ---
    public ObservableCollection<string> ImageNames { get; } = new();
    
    // [FIX CRITICO] Espone i path per BoardViewModel e altri consumatori
    public List<string> ImagePaths => _collection?.Files.Select(f => f.FilePath).ToList() ?? new List<string>();

    public string CurrentImageText => $"{CurrentIndex + 1} / {_collection.Count}";
    public bool CanShowPrevious => !IsAnimating && CurrentIndex > 0;
    public bool CanShowNext => !IsAnimating && CurrentIndex < _collection.Count - 1;
    public string? TemporaryFolderPath { get; set; }

    public MultipleImagesNodeViewModel(
        MultipleImagesNodeModel model,
        IFitsIoService ioService,
        IFitsOpenCvConverter converter,
        IImageAnalysisService analysis,
        IFitsRendererFactory rendererFactory,
        Size maxSize,
        FitsCollection? initialCollection = null) 
        : base(model)
    {
        _ioService = ioService;
        _converter = converter;
        _analysis = analysis;
        _rendererFactory = rendererFactory;
        _multiModel = model;
        
        _maxImageSize = maxSize;

        // 1. Inizializzazione Collezione
        // Se non viene passata (es. caricamento da disco), la creiamo dai path nel Modello
        _collection = initialCollection ?? new FitsCollection(model.ImagePaths, cacheSize: 5);
        
        RefreshUiLists();
        _currentIndex = 0;

        // 2. Timer Animazione
        _animationTimer = new DispatcherTimer(DispatcherPriority.Render);
        _animationTimer.Interval = TimeSpan.FromMilliseconds(AnimationIntervalMs);
        _animationTimer.Tick += OnAnimationTick;
    }

    private void RefreshUiLists()
    {
        ImageNames.Clear();
        foreach (var fileRef in _collection.Files)
        {
            ImageNames.Add(fileRef.FileName);
        }
    }

    public async Task InitializeAsync(bool centerOnPosition = false)
    {
        if (_collection.Count == 0) return;

        // Carica il primo frame
        await LoadImageAtIndexAsync(0, forceProfileReset: true);

        if (centerOnPosition) Viewport.ResetView();
        _ = PrefetchImageAsync(1);
    }

    partial void OnCurrentIndexChanged(int value)
    {
        if (!IsAnimating) _ = LoadImageAtIndexAsync(value);
    }

    // --- LOGICA CORE DI CARICAMENTO ---
    private async Task LoadImageAtIndexAsync(int index, bool forceProfileReset = false)
    {
        if (index < 0 || index >= _collection.Count) return;

        _loadingCts?.Cancel();
        _loadingCts = new CancellationTokenSource();
        var token = _loadingCts.Token;

        try
        {
            // 1. Recupero Dati (Pixel da Cache/Disco + Header)
            var (pixels, header) = await GetOrLoadDataAtIndex(index);
            
            if (token.IsCancellationRequested || pixels == null || header == null) return;

            // 2. Creazione Renderer (Background)
            var newRenderer = _rendererFactory.Create(pixels, header);
            await newRenderer.InitializeAsync();

            if (token.IsCancellationRequested)
            {
                newRenderer.Dispose();
                return;
            }

            // 3. Logica Scientifica Soglie
            AbsoluteContrastProfile? adaptedProfile = null;

            if (!forceProfileReset && ActiveFitsImage != null && !ActiveFitsImage.IsDisposed)
            {
                // Calcoliamo il profilo basandoci sulle statistiche
                var oldStats = ActiveFitsImage.GetImageStatistics();
                var newStats = newRenderer.GetImageStatistics();

                adaptedProfile = _analysis.CalculateAdaptedProfileFromStats(
                    oldStats,
                    ActiveFitsImage.BlackPoint,
                    ActiveFitsImage.WhitePoint,
                    newStats
                );
            }

            // 4. Swap e Applicazione Profilo
            await ApplyNewRendererAsync(newRenderer, adaptedProfile);

            if (token.IsCancellationRequested) return;

            // 5. Aggiornamenti UI
            PreviousImageCommand.NotifyCanExecuteChanged();
            NextImageCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(ActiveFile));

            _ = PrefetchImageAsync(index + 1);
        }
        catch (OperationCanceledException) { }
        finally { if (_loadingCts?.Token == token) _loadingCts = null; }
    }

    [RelayCommand]
    public void ToggleAnimation() => IsAnimating = !IsAnimating;

    partial void OnIsAnimatingChanged(bool value)
    {
        if (value) _animationTimer.Start(); else _animationTimer.Stop();
        OnPropertyChanged(nameof(CanShowPrevious));
        OnPropertyChanged(nameof(CanShowNext));
    }

    private void OnAnimationTick(object? sender, EventArgs e)
    {
        if (_collection.Count == 0) return;
        int nextIndex = (CurrentIndex + 1) % _collection.Count;
        _ = AdvanceFrameAsync(nextIndex);
    }

    private async Task AdvanceFrameAsync(int index)
    {
        await LoadImageAtIndexAsync(index);
        SetProperty(ref _currentIndex, index, nameof(CurrentIndex));
        OnPropertyChanged(nameof(CurrentImageText));
    }

    [RelayCommand(CanExecute = nameof(CanShowPrevious))]
    private void PreviousImage() => CurrentIndex--;
    
    [RelayCommand(CanExecute = nameof(CanShowNext))]
    private void NextImage() => CurrentIndex++;

    // --- Implementazione Metodi Astratti Nuovi ---

    public override async Task LoadInputAsync(FitsCollection input)
    {
        _collection = input;
        RefreshUiLists();
        
        if (_collection.Count > 0)
        {
            _currentIndex = 0;
            OnPropertyChanged(nameof(CurrentIndex));
            OnPropertyChanged(nameof(CurrentImageText));
            OnPropertyChanged(nameof(ImagePaths)); // Notifica cambio paths
            
            await LoadImageAtIndexAsync(0, forceProfileReset: true);
        }
    }

    public override async Task RefreshDataFromDiskAsync()
    {
        _collection.PixelCache.Clear();
        await LoadImageAtIndexAsync(CurrentIndex);
    }

    // --- Helpers Gestione Dati ---

    private async Task<(Array? Pixels, FitsHeader? Header)> GetOrLoadDataAtIndex(int index)
    {
        if (index < 0 || index >= _collection.Count) return (null, null);
        
        var fileRef = _collection[index];
        Array? pixels = null;
        FitsHeader? header = null;

        if (fileRef.HasUnsavedChanges)
            header = fileRef.UnsavedHeader;
        else
            header = await _ioService.ReadHeaderAsync(fileRef.FilePath);

        if (_collection.PixelCache.TryGet(fileRef.FilePath, out var cachedPixels))
        {
            pixels = cachedPixels;
        }
        else
        {
            pixels = await _ioService.ReadPixelDataAsync(fileRef.FilePath);
            if (pixels != null)
                _collection.PixelCache.Add(fileRef.FilePath, pixels);
        }

        return (pixels, header);
    }

    private async Task PrefetchImageAsync(int nextIndex)
    {
        if (nextIndex >= _collection.Count) nextIndex = 0;
        
        var fileRef = _collection[nextIndex];
        if (_collection.PixelCache.TryGet(fileRef.FilePath, out _)) return;

        await Task.Run(async () => 
        {
            var pixels = await _ioService.ReadPixelDataAsync(fileRef.FilePath);
            if (pixels != null)
                _collection.PixelCache.Add(fileRef.FilePath, pixels);
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _animationTimer.Stop();
            _loadingCts?.Cancel();
            ActiveFitsImage?.Dispose();
            _collection.PixelCache.Clear();
            
            if (!string.IsNullOrEmpty(TemporaryFolderPath) && Directory.Exists(TemporaryFolderPath))
            {
                try { Directory.Delete(TemporaryFolderPath, true); } catch { }
            }
        }
        base.Dispose(disposing);
    }
}