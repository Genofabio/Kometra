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
    private readonly LruCache<int, FitsImageData> _dataCache = new(3);
    private Size _maxImageSize;
    
    // Memorizza le preferenze di contrasto (Sigma/Assoluto) durante lo scorrimento
    private ContrastProfile? _lastContrastProfile;

    // --- Proprietà Observable ---

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveRenderer))] // Notifica la base che il renderer è cambiato
    private FitsRenderer? _activeFitsImage;

    // Implementazione astratta della base
    public override FitsRenderer? ActiveRenderer => ActiveFitsImage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentImageText))]
    [NotifyPropertyChangedFor(nameof(CanShowPrevious))]
    [NotifyPropertyChangedFor(nameof(CanShowNext))]
    private int _currentIndex;

    public ObservableCollection<string> ImageNames { get; } = new();
    
    public string? TemporaryFolderPath { get; set; }

    // --- Proprietà Esposte (Sola Lettura) ---
    public List<string> ImagePaths => _multiModel.ImagePaths;
    public Size MaxImageSize => _maxImageSize;
    public string CurrentImageText => $"{CurrentIndex + 1} / {_imageCount}";
    public bool CanShowPrevious => CurrentIndex > 0;
    public bool CanShowNext => CurrentIndex < _imageCount - 1;

    // Override dimensione contenuto
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

    // --- Inizializzazione ---
    public async Task InitializeAsync()
    {
        _dataCache.TryGet(0, out var initialData);
        
        if (initialData == null)
        {
            Debug.WriteLine("ERR: Dati iniziali null in InitializeAsync.");
            return;
        }

        ActiveFitsImage = new FitsRenderer(initialData, _fitsService, _converter, _analysis);
        await ActiveFitsImage.InitializeAsync();
        
        // Setup iniziale Viewport
        Viewport.ImageSize = ActiveFitsImage.ImageSize;
        Viewport.ResetView(); // 1:1

        // Inizializza il profilo di contrasto basato sull'auto-stretch iniziale
        _lastContrastProfile = ActiveFitsImage.CaptureContrastProfile();
        
        // Sincronizza UI (Sliders)
        BlackPoint = ActiveFitsImage.BlackPoint;
        WhitePoint = ActiveFitsImage.WhitePoint;
        
        _ = PrefetchImageAsync(1);
    }

    partial void OnCurrentIndexChanged(int value)
    {
        _ = LoadImageAtIndexAsync(value);
    }

    // --- Logica Caricamento e Swap Immagini ---
    private async Task LoadImageAtIndexAsync(int index)
    {
        if (index < 0 || index >= _imageCount) return;
        
        // Se l'immagine è già quella attiva, usciamo
        var cachedData = await GetOrLoadDataAtIndex(index);
        if (ActiveFitsImage != null && ActiveFitsImage.Data == cachedData) return;
        if (cachedData == null) return;

        // 1. Cattura il profilo di contrasto corrente prima di scaricare il vecchio renderer
        if (ActiveFitsImage != null)
        {
            _lastContrastProfile = ActiveFitsImage.CaptureContrastProfile();
            ActiveFitsImage.UnloadData();
        }

        // 2. Crea il nuovo renderer
        var newFitsImage = new FitsRenderer(cachedData, _fitsService, _converter, _analysis);
        await newFitsImage.InitializeAsync(); 

        // 3. Applica il profilo di contrasto (Sigma Lock o Assoluto)
        if (_lastContrastProfile != null)
        {
            newFitsImage.ApplyContrastProfile(_lastContrastProfile);
        }

        // 4. Aggiorna Viewport (solo dimensioni, manteniamo zoom/pan correnti)
        Viewport.ImageSize = newFitsImage.ImageSize;

        // 5. Scambio Renderer
        ActiveFitsImage = newFitsImage;

        // 6. Aggiorna Slider UI con i nuovi valori calcolati
        BlackPoint = newFitsImage.BlackPoint; 
        WhitePoint = newFitsImage.WhitePoint;
    
        PreviousImageCommand.NotifyCanExecuteChanged();
        NextImageCommand.NotifyCanExecuteChanged();
        
        _ = PrefetchImageAsync(index + 1);
    }

    // --- Comandi Navigazione ---
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

    // --- Implementazione Metodi Astratti ---

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
                
                // Se i dati cambiano radicalmente (es. dopo uno stack), resettiamo la vista 1:1
                Viewport.ImageSize = _maxImageSize;
                Viewport.ResetView();

                OnPropertyChanged(nameof(MaxImageSize));
                OnPropertyChanged(nameof(NodeContentSize)); 
                OnPropertyChanged(nameof(EstimatedTotalSize));
            }
        }
        
        // Reset profilo contrasto per i nuovi dati
        _lastContrastProfile = null;

        // Ricarica la vista corrente
        int tempIndex = CurrentIndex;
        CurrentIndex = -1; 
        CurrentIndex = tempIndex;
    }
    
    public override Task<List<string>> PrepareInputPathsAsync(IFitsService fitsService)
    {
        return Task.FromResult(new List<string>(ImagePaths));
    }

    public override FitsImageData? GetActiveImageData()
    {
        return ActiveFitsImage?.Data;
    }

    // --- Helpers Cache & IO ---

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

    // --- Dispose ---
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ActiveFitsImage?.UnloadData();
            _dataCache.Clear(); 
        }
        
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