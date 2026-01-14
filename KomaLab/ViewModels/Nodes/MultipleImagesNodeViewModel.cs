using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
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
using KomaLab.Services.Utilities;
using KomaLab.ViewModels.Visualization;

namespace KomaLab.ViewModels.Nodes;

/// <summary>
/// ViewModel per la gestione di stack di immagini FITS.
/// Gestisce l'animazione, la cache LRU e l'adattamento scientifico del contrasto tra frame.
/// </summary>
public partial class MultipleImagesNodeViewModel : ImageNodeViewModel
{
    private const int AnimationIntervalMs = 250;

    private readonly IFitsIoService _ioService;
    private readonly IFitsImageDataConverter _converter;
    private readonly IImageAnalysisService _analysis;
    private readonly IFitsRendererFactory _rendererFactory; 
    private readonly MultipleImagesNodeModel _multiModel;

    private readonly int _imageCount;
    private readonly LruCache<int, FitsImageData> _dataCache = new(3);
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
    [NotifyPropertyChangedFor(nameof(CurrentImageText), nameof(CanShowPrevious), nameof(CanShowNext))]
    private int _currentIndex;

    // --- Implementazione ImageNodeViewModel ---
    public override FitsRenderer? ActiveRenderer => ActiveFitsImage;
    public override Size NodeContentSize => _maxImageSize;

    protected override void OnRendererSwapping(FitsRenderer newRenderer) 
        => ActiveFitsImage = newRenderer;

    // --- Proprietà UI ---
    public ObservableCollection<string> ImageNames { get; } = new();
    public List<string> ImagePaths => _multiModel.ImagePaths;
    public string CurrentImageText => $"{CurrentIndex + 1} / {_imageCount}";
    public bool CanShowPrevious => !IsAnimating && CurrentIndex > 0;
    public bool CanShowNext => !IsAnimating && CurrentIndex < _imageCount - 1;
    public string? TemporaryFolderPath { get; set; }

    public MultipleImagesNodeViewModel(
        MultipleImagesNodeModel model,
        IFitsIoService ioService,
        IFitsImageDataConverter converter,
        IImageAnalysisService analysis,
        IFitsRendererFactory rendererFactory,
        Size maxSize, 
        FitsImageData? initialData) 
        : base(model)
    {
        _ioService = ioService;
        _converter = converter;
        _analysis = analysis;
        _rendererFactory = rendererFactory;
        _multiModel = model;
        
        _maxImageSize = maxSize;
        _imageCount = model.ImagePaths.Count; 
        
        foreach (var path in model.ImagePaths)
            ImageNames.Add(Path.GetFileName(path));

        _currentIndex = 0;
        
        // Inizializzazione timer senza auto-start
        _animationTimer = new DispatcherTimer(DispatcherPriority.Render);
        _animationTimer.Interval = TimeSpan.FromMilliseconds(AnimationIntervalMs);
        _animationTimer.Tick += OnAnimationTick;
        
        if (initialData != null)
            _dataCache.Add(0, initialData);
    }

    public async Task InitializeAsync(bool centerOnPosition = false)
    {
        var cachedData = await GetOrLoadDataAtIndex(0);
        if (cachedData == null) return;

        // Primo caricamento: usa soglie di default del renderer
        await ApplyNewRendererAsync(_rendererFactory.Create(cachedData));
        
        if (centerOnPosition) Viewport.ResetView();
        _ = PrefetchImageAsync(1);
    }

    partial void OnCurrentIndexChanged(int value)
    {
        if (!IsAnimating) _ = LoadImageAtIndexAsync(value);
    }

    private async Task LoadImageAtIndexAsync(int index)
    {
        if (index < 0 || index >= _imageCount) return;

        _loadingCts?.Cancel();
        _loadingCts = new CancellationTokenSource();
        var token = _loadingCts.Token;

        try
        {
            var cachedData = await GetOrLoadDataAtIndex(index);
            if (token.IsCancellationRequested || cachedData == null) return;
            if (ActiveFitsImage?.Data == cachedData) return;

            // --- LOGICA SCIENTIFICA SOGLIE ---
            AbsoluteContrastProfile? adaptedProfile = null;
            if (ActiveFitsImage != null && !ActiveFitsImage.IsDisposed)
            {
                // Adattiamo le soglie ADU dal frame corrente al nuovo
                adaptedProfile = _analysis.CalculateAdaptedProfile(
                    ActiveFitsImage.Data, cachedData, BlackPoint, WhitePoint);
            }

            var newRenderer = _rendererFactory.Create(cachedData);
            
            // Passiamo il profilo alla base affinché non lo sovrascriva con quello vecchio
            await ApplyNewRendererAsync(newRenderer, adaptedProfile);

            if (token.IsCancellationRequested) return;

            PreviousImageCommand.NotifyCanExecuteChanged();
            NextImageCommand.NotifyCanExecuteChanged();
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
        int nextIndex = (CurrentIndex + 1) % _imageCount;
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

    public override async Task ApplyProcessedDataAsync(List<FitsImageData> newProcessedData)
    {
        _dataCache.Clear();
        for (int i = 0; i < newProcessedData.Count; i++)
            _dataCache.Add(i, newProcessedData[i]);

        if (newProcessedData.Count > 0)
        {
            var first = newProcessedData[0];
            _maxImageSize = new Size(first.Width, first.Height);
            await ApplyNewRendererAsync(_rendererFactory.Create(first));
            Viewport.ResetView();
        }
    }

    public override async Task<List<FitsImageData?>> GetCurrentDataAsync()
    {
        var fullList = new List<FitsImageData?>();
        for (int i = 0; i < _imageCount; i++)
            fullList.Add(await GetOrLoadDataAtIndex(i));
        return fullList;
    }

    public override FitsImageData? GetActiveImageData() => ActiveFitsImage?.Data;

    private async Task<FitsImageData?> GetOrLoadDataAtIndex(int index)
    {
        if (index < 0 || index >= _imageCount) return null;
        if (_dataCache.TryGet(index, out var cachedData)) return cachedData;
        
        try
        {
            var data = await _ioService.LoadAsync(ImagePaths[index]);
            if (data != null) _dataCache.Add(index, data);
            return data;
        }
        catch { return null; }
    }

    private async Task PrefetchImageAsync(int nextIndex)
    {
        if (nextIndex >= _imageCount || _dataCache.TryGet(nextIndex, out _)) return;
        await Task.Run(() => GetOrLoadDataAtIndex(nextIndex));
    }
    
    public override async Task RefreshDataFromDiskAsync()
    {
        _dataCache.Clear();
        await LoadImageAtIndexAsync(CurrentIndex);
    }

    public override Task<List<string>> PrepareInputPathsAsync(IFitsIoService ioService) 
        => Task.FromResult(new List<string>(ImagePaths));

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _animationTimer.Stop();
            _loadingCts?.Cancel();
            ActiveFitsImage?.Dispose();
            _dataCache.Clear(); 
            
            if (!string.IsNullOrEmpty(TemporaryFolderPath) && Directory.Exists(TemporaryFolderPath))
            {
                try { Directory.Delete(TemporaryFolderPath, true); } catch { }
            }
        }
        base.Dispose(disposing);
    }
}