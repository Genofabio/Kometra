using System;
using System.Collections.Generic;
using System.Collections.ObjectModel; 
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KomaLab.Models;
using KomaLab.Services;
using KomaLab.Services.Data;
using KomaLab.Services.Imaging;
using KomaLab.Services.Utilities;
using KomaLab.ViewModels.Helpers;

namespace KomaLab.ViewModels;

public partial class MultipleImagesNodeViewModel : ImageNodeViewModel
{
    // --- Dipendenze ---
    private readonly IFitsService _fitsService;
    private readonly IFitsDataConverter _converter;
    private readonly IImageAnalysisService _analysis;
    
    private readonly MultipleImagesNodeModel _multiModel;
    
    // --- Stato Interno ---
    private readonly int _imageCount;
    
    // CACHE LRU (Memoria limitata a 3 immagini + Prefetch)
    private readonly LruCache<int, FitsImageData> _dataCache = new(3);
    
    private Size _maxImageSize; 

    // --- Sigma Locking ---
    private bool _hasLockedThresholds;
    private double _lockedKBlack; 
    private double _lockedKWhite; 

    // --- Proprietà Observable ---
    
    [ObservableProperty]
    private FitsRenderer? _activeFitsImage;

    public ObservableCollection<string> ImageNames { get; } = new();

    // Proprietà per la pulizia dei file temporanei (allineamento)
    public string? TemporaryFolderPath { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentImageText))]
    [NotifyPropertyChangedFor(nameof(CanShowPrevious))]
    [NotifyPropertyChangedFor(nameof(CanShowNext))]
    private int _currentIndex;
    
    [ObservableProperty] private double _blackPoint;
    [ObservableProperty] private double _whitePoint;

    // --- Proprietà Esposte ---
    public List<string> ImagePaths => _multiModel.ImagePaths;
    public Size MaxImageSize => _maxImageSize;
    public string CurrentImageText => $"{CurrentIndex + 1} / {_imageCount}";
    public bool CanShowPrevious => CurrentIndex > 0;
    public bool CanShowNext => CurrentIndex < _imageCount - 1;

    // --- Override da ImageNodeViewModel ---
    protected override Size NodeContentSize => _maxImageSize;

    // --- Costruttore ---
    public MultipleImagesNodeViewModel(
        MultipleImagesNodeModel model,
        IFitsService fitsService,
        IFitsDataConverter converter,
        IImageAnalysisService analysis,
        Size maxSize, 
        FitsImageData? initialData) 
        : base(model)
    {
        _fitsService = fitsService;
        _converter = converter;
        _analysis = analysis;
        _multiModel = model;
        _maxImageSize = maxSize;
        _imageCount = model.ImagePaths.Count; 
        
        foreach (var path in model.ImagePaths)
        {
            ImageNames.Add(Path.GetFileName(path));
        }

        _currentIndex = 0;
        
        if (initialData != null)
        {
            _dataCache.Add(0, initialData);
        }
    }

    // --- Initialize ---
    public async Task InitializeAsync()
    {
        _dataCache.TryGet(0, out var initialData);
        
        if (initialData == null)
        {
            Debug.WriteLine("ERR: Dati iniziali null in InitializeAsync.");
            return;
        }

        ActiveFitsImage = new FitsRenderer(
            initialData, 
            _fitsService, 
            _converter, 
            _analysis);
        
        await ActiveFitsImage.InitializeAsync();
        
        var (mean, sigma) = ActiveFitsImage.GetImageStatistics();
        _lockedKBlack = (ActiveFitsImage.BlackPoint - mean) / sigma;
        _lockedKWhite = (ActiveFitsImage.WhitePoint - mean) / sigma;
        _hasLockedThresholds = true;

        BlackPoint = ActiveFitsImage.BlackPoint;
        WhitePoint = ActiveFitsImage.WhitePoint;
        
        _ = PrefetchImageAsync(1);
    }

    partial void OnCurrentIndexChanged(int value)
    {
        _ = LoadImageAtIndexAsync(value);
    }

    // --- Comandi ---
    [RelayCommand(CanExecute = nameof(CanShowPrevious))]
    private void PreviousImage()
    {
        if (!IsSelected) IsSelected = true;
        if (CurrentIndex > 0) CurrentIndex--;
    }
    
    [RelayCommand(CanExecute = nameof(CanShowNext))]
    private void NextImage()
    {
        if (!IsSelected) IsSelected = true;
        if (CurrentIndex < _imageCount - 1) CurrentIndex++;
    }
    
    [RelayCommand(CanExecute = nameof(CanResetThresholds))]
    private async Task ResetThresholds()
    {
        if (ActiveFitsImage == null) return;
        await ActiveFitsImage.ResetThresholdsAsync();
        BlackPoint = ActiveFitsImage.BlackPoint;
        WhitePoint = ActiveFitsImage.WhitePoint;
        UpdateLockedSigmaFactors();
    }
    private bool CanResetThresholds() => ActiveFitsImage != null;

    // --- Logica Loading & Sigma Lock ---
    private async Task LoadImageAtIndexAsync(int index)
    {
        if (index < 0 || index >= _imageCount) return;
        if (index == CurrentIndex && ActiveFitsImage != null && ActiveFitsImage.Data == await GetOrLoadDataAtIndex(index)) return;
    
        FitsImageData? dataToShow = await GetOrLoadDataAtIndex(index);
        if (dataToShow == null) return;

        var newFitsImage = new FitsRenderer(
            dataToShow, 
            _fitsService, 
            _converter, 
            _analysis);

        await newFitsImage.InitializeAsync(); 

        var (mean, sigma) = newFitsImage.GetImageStatistics();

        if (!_hasLockedThresholds)
        {
            _lockedKBlack = (newFitsImage.BlackPoint - mean) / sigma;
            _lockedKWhite = (newFitsImage.WhitePoint - mean) / sigma;
            _hasLockedThresholds = true;
        }
        else
        {
            newFitsImage.BlackPoint = mean + (_lockedKBlack * sigma);
            newFitsImage.WhitePoint = mean + (_lockedKWhite * sigma);
        }

        var oldFitsImage = ActiveFitsImage;
        ActiveFitsImage = newFitsImage;
        oldFitsImage?.UnloadData();

        BlackPoint = newFitsImage.BlackPoint; 
        WhitePoint = newFitsImage.WhitePoint;
    
        PreviousImageCommand.NotifyCanExecuteChanged();
        NextImageCommand.NotifyCanExecuteChanged();
        
        _ = PrefetchImageAsync(index + 1);
    }

    // --- Metodi Abstract Implementati ---
    public override async Task<List<FitsImageData?>> GetCurrentDataAsync()
    {
        var fullList = new List<FitsImageData?>();
        for (int i = 0; i < _imageCount; i++)
        {
            fullList.Add(await GetOrLoadDataAtIndex(i));
        }
        return fullList;
    }

    public override async Task ApplyProcessedDataAsync(List<FitsImageData> newProcessedData)
    {
        _dataCache.Clear();
        for (int i = 0; i < newProcessedData.Count; i++)
        {
            _dataCache.Add(i, newProcessedData[i]);
        }

        if (newProcessedData.Count > 0)
        {
            var first = newProcessedData[0];
            if (first is { Width: > 0, Height: > 0 })
            {
                _maxImageSize = new Size(first.Width, first.Height);
                OnPropertyChanged(nameof(MaxImageSize));
                OnPropertyChanged(nameof(NodeContentSize)); 
                OnPropertyChanged(nameof(EstimatedTotalSize));
            }
        }

        _hasLockedThresholds = false; 
        
        int tempIndex = CurrentIndex;
        if (tempIndex == 0) CurrentIndex = -1; else CurrentIndex = 0; 
        CurrentIndex = tempIndex; 
        
        await LoadImageAtIndexAsync(tempIndex);
    }
    
    // --- IMPLEMENTAZIONE PREPARE INPUT PATHS ---
    // Questo è fondamentale per l'allineamento Low RAM
    public override Task<List<string>> PrepareInputPathsAsync(IFitsService fitsService)
    {
        // Restituisce semplicemente i path che ha già, non serve salvare nulla
        return Task.FromResult(new List<string>(ImagePaths));
    }

    public override async Task ResetThresholdsAsync()
    {
        await ResetThresholds();
    }
    
    public override FitsImageData? GetActiveImageData()
    {
        return ActiveFitsImage?.Data;
    }

    // --- Helpers ---
    private async Task<FitsImageData?> GetOrLoadDataAtIndex(int index)
    {
        if (index < 0 || index >= _imageCount) return null;
        if (_dataCache.TryGet(index, out var cachedData)) return cachedData;
        return await LoadDataFromDiskAsync(index);
    }

    private async Task<FitsImageData?> LoadDataFromDiskAsync(int index)
    {
        try
        {
            var data = await _fitsService.LoadFitsFromFileAsync(_multiModel.ImagePaths[index]);
            if (data != null) _dataCache.Add(index, data);
            return data;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error loading {index}: {ex.Message}");
            return null;
        }
    }

    private async Task PrefetchImageAsync(int nextIndex)
    {
        if (nextIndex >= _imageCount) return;
        if (_dataCache.TryGet(nextIndex, out _)) return;

        await Task.Run(async () =>
        {
            await LoadDataFromDiskAsync(nextIndex);
        });
    }

    partial void OnBlackPointChanged(double value)
    {
        if (ActiveFitsImage != null)
        {
            ActiveFitsImage.BlackPoint = value;
            UpdateLockedSigmaFactors();
        }
    }
    
    partial void OnWhitePointChanged(double value)
    {
        if (ActiveFitsImage != null)
        {
            ActiveFitsImage.WhitePoint = value;
            UpdateLockedSigmaFactors();
        }
    }

    private void UpdateLockedSigmaFactors()
    {
        if (ActiveFitsImage == null) return;
        var (mean, sigma) = ActiveFitsImage.GetImageStatistics();
        _lockedKBlack = (BlackPoint - mean) / sigma;
        _lockedKWhite = (WhitePoint - mean) / sigma;
        _hasLockedThresholds = true;
    }
    
    // --- Dispose ---
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ActiveFitsImage?.UnloadData();
            _dataCache.Clear(); 
        }
        
        // --- PULIZIA DISCO ---
        if (!string.IsNullOrEmpty(TemporaryFolderPath) && Directory.Exists(TemporaryFolderPath))
        {
            try
            {
                Directory.Delete(TemporaryFolderPath, true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DISK CLEANUP ERROR] Impossibile eliminare temp: {ex.Message}");
            }
        }
        
        base.Dispose(disposing);
    }
}