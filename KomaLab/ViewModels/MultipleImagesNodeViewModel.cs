using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia; // Per Size
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KomaLab.Models;
using KomaLab.Services;
using KomaLab.ViewModels.Helpers;

namespace KomaLab.ViewModels;

public partial class MultipleImagesNodeViewModel : ImageNodeViewModel
{
    // --- Dipendenze ---
    private readonly IFitsService _fitsService;
    
    // NUOVE DIPENDENZE
    private readonly IFitsDataConverter _converter;
    private readonly IImageAnalysisService _analysis;
    
    private readonly MultipleImagesNodeModel _multiModel;
    
    // --- Stato Interno ---
    private readonly int _imageCount;
    private List<FitsImageData?> _processedDataCache;
    private Size _maxImageSize; // Avalonia.Size per la UI

    // --- Sigma Locking ---
    private bool _hasLockedThresholds;
    private double _lockedKBlack; 
    private double _lockedKWhite; 

    // --- Proprietà Observable ---
    
    [ObservableProperty]
    private FitsRenderer? _activeFitsImage;

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
        IFitsDataConverter converter,      // <--- NUOVO
        IImageAnalysisService analysis,    // <--- NUOVO
        Size maxSize, 
        FitsImageData initialData) 
        : base(model)
    {
        _fitsService = fitsService;
        _converter = converter;
        _analysis = analysis;
        _multiModel = model;
        _maxImageSize = maxSize;
        _imageCount = model.ImagePaths.Count; 
        
        _currentIndex = 0;
        
        // Inizializza cache
        _processedDataCache = Enumerable.Repeat<FitsImageData?>(null, _imageCount).ToList();
        _processedDataCache[0] = initialData;
    }

    /// <summary>
    /// Metodo di inizializzazione asincrona post-costruttore.
    /// </summary>
    public async Task InitializeAsync()
    {
        var initialData = _processedDataCache[0];
        if (initialData == null)
        {
            Debug.WriteLine("ERR: Dati iniziali null in InitializeAsync.");
            return;
        }

        // Creazione Renderer con i nuovi servizi
        ActiveFitsImage = new FitsRenderer(
            initialData, 
            _fitsService, 
            _converter, 
            _analysis);
        
        // 1. Inizializza renderer
        await ActiveFitsImage.InitializeAsync();
        
        // 2. Calcola statistiche iniziali
        var (mean, sigma) = ActiveFitsImage.GetImageStatistics();
        
        // 3. Calcola fattori K iniziali
        _lockedKBlack = (ActiveFitsImage.BlackPoint - mean) / sigma;
        _lockedKWhite = (ActiveFitsImage.WhitePoint - mean) / sigma;
        _hasLockedThresholds = true;

        // 4. Sincronizza UI
        BlackPoint = ActiveFitsImage.BlackPoint;
        WhitePoint = ActiveFitsImage.WhitePoint;
    }

    // --- Comandi ---

    [RelayCommand(CanExecute = nameof(CanShowPrevious))]
    private async Task PreviousImage()
    {
        if (!IsSelected) IsSelected = true;
        await LoadImageAtIndexAsync(CurrentIndex - 1);
    }
    
    [RelayCommand(CanExecute = nameof(CanShowNext))]
    private async Task NextImage()
    {
        if (!IsSelected) IsSelected = true;
        await LoadImageAtIndexAsync(CurrentIndex + 1);
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
        if (index < 0 || index >= _imageCount || index == CurrentIndex) return;
    
        FitsImageData? dataToShow = await GetOrLoadDataAtIndex(index);
        if (dataToShow == null) return;

        // 1. Nuovo renderer con i servizi corretti
        var newFitsImage = new FitsRenderer(
            dataToShow, 
            _fitsService, 
            _converter, 
            _analysis);

        await newFitsImage.InitializeAsync(); 

        // 2. Logica Sigma Locking
        var (mean, sigma) = newFitsImage.GetImageStatistics();

        if (!_hasLockedThresholds)
        {
            _lockedKBlack = (newFitsImage.BlackPoint - mean) / sigma;
            _lockedKWhite = (newFitsImage.WhitePoint - mean) / sigma;
            _hasLockedThresholds = true;
        }
        else
        {
            double adaptedBlack = mean + (_lockedKBlack * sigma);
            double adaptedWhite = mean + (_lockedKWhite * sigma);
            
            newFitsImage.BlackPoint = adaptedBlack;
            newFitsImage.WhitePoint = adaptedWhite;
        }

        // 3. Swap
        CurrentIndex = index;
        var oldFitsImage = ActiveFitsImage;
        ActiveFitsImage = newFitsImage;
        oldFitsImage?.UnloadData();

        // 4. Update UI
        BlackPoint = newFitsImage.BlackPoint; 
        WhitePoint = newFitsImage.WhitePoint;
    
        PreviousImageCommand.NotifyCanExecuteChanged();
        NextImageCommand.NotifyCanExecuteChanged();
    }

    // --- Metodi Abstract Implementati ---

    public override async Task<List<FitsImageData?>> GetCurrentDataAsync()
    {
        for (int i = 0; i < _imageCount; i++)
        {
            if (_processedDataCache[i] == null)
            {
                await GetOrLoadDataAtIndex(i);
            }
        }
        return _processedDataCache;
    }

    public override async Task ApplyProcessedDataAsync(List<FitsImageData> newProcessedData)
    {
        _processedDataCache = newProcessedData.Cast<FitsImageData?>().ToList();

        if (newProcessedData.Count > 0)
        {
            var first = newProcessedData[0];
            // Pattern matching sicuro per width/height > 0
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
        CurrentIndex = -1; 
        await LoadImageAtIndexAsync(tempIndex);
    }
    
    public override async Task ResetThresholdsAsync()
    {
        await ResetThresholds();
    }
    
    public override FitsImageData? GetActiveImageData()
    {
        if (CurrentIndex >= 0 && CurrentIndex < _processedDataCache.Count)
        {
            return _processedDataCache[CurrentIndex];
        }
        return null;
    }

    // --- Helpers ---

    private async Task<FitsImageData?> GetOrLoadDataAtIndex(int index)
    {
        if (index < 0 || index >= _imageCount) return null;

        if (_processedDataCache[index] == null)
        {
            try
            {
                var data = await _fitsService.LoadFitsFromFileAsync(_multiModel.ImagePaths[index]);
                if (data != null) _processedDataCache[index] = data;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error loading {index}: {ex.Message}");
            }
        }
        return _processedDataCache[index];
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
}