using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KomaLab.Models.Fits;
using KomaLab.Models.Nodes;
using KomaLab.Models.Visualization;
using KomaLab.Services.Data;
using KomaLab.Services.Imaging;
using KomaLab.Services.Utilities;
using KomaLab.ViewModels.Visualization;

namespace KomaLab.ViewModels.Nodes;

// ---------------------------------------------------------------------------
// FILE: MultipleImagesNodeViewModel.cs
// RUOLO: Nodo per sequenze di immagini (Animazioni/Stacking)
// DESCRIZIONE:
// Gestisce una lista di file FITS, permettendo la navigazione sequenziale
// e l'animazione. Utilizza una LRU Cache per limitare la RAM.
// ---------------------------------------------------------------------------

public partial class MultipleImagesNodeViewModel : ImageNodeViewModel
{
    // --- Dipendenze Enterprise ---
    private readonly IFitsIoService _ioService;
    private readonly IFitsImageDataConverter _converter;
    private readonly IImageAnalysisService _analysis;
    private readonly MultipleImagesNodeModel _multiModel;

    // --- Stato Interno ---
    private readonly int _imageCount;
    private readonly LruCache<int, FitsImageData> _dataCache = new(3); // Cache piccola per risparmiare RAM
    private CancellationTokenSource? _loadingCts;
    private Size _maxImageSize;
    
    // Memorizza le preferenze di contrasto (Sigma/Assoluto) durante lo scorrimento
    private ContrastProfile? _lastContrastProfile;

    // --- Proprietà Observable ---

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveRenderer))] 
    private FitsRenderer? _activeFitsImage;
    
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanShowPrevious))]
    [NotifyPropertyChangedFor(nameof(CanShowNext))]
    [NotifyCanExecuteChangedFor(nameof(PreviousImageCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextImageCommand))]
    private bool _isAnimating;
    
    private const int AnimationDelayMs = 150;

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
    public bool CanShowPrevious => !IsAnimating && CurrentIndex > 0;
    public bool CanShowNext => !IsAnimating && CurrentIndex < _imageCount - 1;

    // Override dimensione contenuto
    protected override Size NodeContentSize => _maxImageSize;

    // --- Costruttore ---
    public MultipleImagesNodeViewModel(
        MultipleImagesNodeModel model,
        IFitsIoService ioService,
        IFitsImageDataConverter converter,
        IImageAnalysisService analysis,
        Size maxSize, 
        FitsImageData? initialData) 
        : base(model)
    {
        _ioService = ioService ?? throw new ArgumentNullException(nameof(ioService));
        _converter = converter ?? throw new ArgumentNullException(nameof(converter));
        _analysis = analysis ?? throw new ArgumentNullException(nameof(analysis));
        _multiModel = model;
        _maxImageSize = maxSize;
        _imageCount = model.ImagePaths.Count; 
        
        foreach (var path in model.ImagePaths)
        {
            ImageNames.Add(Path.GetFileName(path));
        }

        _currentIndex = 0;
        
        // Popola cache iniziale se disponibile
        if (initialData != null)
        {
            _dataCache.Add(0, initialData);
        }
    }

    // --- Inizializzazione ---
    public async Task InitializeAsync(bool centerOnPosition = false)
    {
        _dataCache.TryGet(0, out var initialData);
        
        if (initialData == null)
        {
            Debug.WriteLine("ERR: Dati iniziali null in InitializeAsync.");
            return;
        }

        // Creazione Renderer con le nuove dipendenze
        ActiveFitsImage = new FitsRenderer(initialData, _ioService, _converter, _analysis);
        await ActiveFitsImage.InitializeAsync();
        
        // Setup iniziale: Modo e Viewport
        ActiveFitsImage.VisualizationMode = this.VisualizationMode;
        Viewport.ImageSize = ActiveFitsImage.ImageSize;
        if (centerOnPosition) Viewport.ResetView(); 

        // Inizializza il profilo di contrasto basato sull'auto-stretch iniziale
        _lastContrastProfile = ActiveFitsImage.CaptureContrastProfile();
        
        // Sincronizza UI (Sliders)
        BlackPoint = ActiveFitsImage.BlackPoint;
        WhitePoint = ActiveFitsImage.WhitePoint;
        
        // Caricamento speculativo del prossimo frame
        _ = PrefetchImageAsync(1);
    }

    // Trigger quando cambia l'indice (se non stiamo animando)
    partial void OnCurrentIndexChanged(int value)
    {
        if (!IsAnimating) 
        {
            // Lanciamo il caricamento in "fire-and-forget" safe
            _ = LoadImageAtIndexAsync(value);
        }
    }

    // --- Logica Caricamento e Swap Immagini ---
    private async Task LoadImageAtIndexAsync(int index)
    {
        if (index < 0 || index >= _imageCount) return;

        // Debounce: Annulla caricamento precedente
        _loadingCts?.Cancel();
        _loadingCts = new CancellationTokenSource();
        var token = _loadingCts.Token;

        try
        {
            // 1. Recupero dati (Cache o Disco)
            var cachedData = await GetOrLoadDataAtIndex(index);
            
            if (token.IsCancellationRequested) return;

            // Se l'immagine è già quella attiva o nulla, esci
            if (ActiveFitsImage != null && ActiveFitsImage.Data == cachedData) return;
            if (cachedData == null) return;

            // 2. Gestione Profilo Contrasto (Persistenza tra frame)
            ContrastProfile? profileToApply = _lastContrastProfile;

            // Se il renderer attuale è valido, catturiamo il suo profilo corrente
            // (così se l'utente ha modificato il contrasto, lo manteniamo nel prossimo frame)
            if (ActiveFitsImage != null && !ActiveFitsImage.IsDisposed) 
            {
                profileToApply = ActiveFitsImage.CaptureContrastProfile();
                _lastContrastProfile = profileToApply;
            }

            // 3. Creazione Nuovo Renderer
            var newFitsImage = new FitsRenderer(cachedData, _ioService, _converter, _analysis);
            
            // Inizializzazione asincrona
            await newFitsImage.InitializeAsync();

            // Applicazione impostazioni globali
            newFitsImage.VisualizationMode = this.VisualizationMode;

            if (token.IsCancellationRequested)
            {
                newFitsImage.UnloadData();
                return;
            }

            // Applicazione Profilo Contrasto
            if (profileToApply != null)
            {
                newFitsImage.ApplyContrastProfile(profileToApply);
            }

            // 4. Swap Sicuro
            var oldImage = ActiveFitsImage;
            
            // Viewport update (importante se le dimensioni cambiano, ma in stack solitamente no)
            Viewport.ImageSize = newFitsImage.ImageSize;

            ActiveFitsImage = newFitsImage; 

            // Dispose immediato della vecchia immagine per liberare VRAM
            oldImage?.UnloadData();

            // Sincronizza UI
            BlackPoint = newFitsImage.BlackPoint;
            WhitePoint = newFitsImage.WhitePoint;

            // Aggiorna stato comandi
            PreviousImageCommand.NotifyCanExecuteChanged();
            NextImageCommand.NotifyCanExecuteChanged();

            // Prefetch prossimo frame
            _ = PrefetchImageAsync(index + 1);
        }
        catch (OperationCanceledException) { /* Ignora */ }
        catch (Exception ex)
        {
            Debug.WriteLine($"Errore LoadImageAtIndex: {ex.Message}");
        }
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
                
                Viewport.ImageSize = _maxImageSize;
                Viewport.ResetView();

                OnPropertyChanged(nameof(MaxImageSize));
                OnPropertyChanged(nameof(NodeContentSize)); 
                OnPropertyChanged(nameof(EstimatedTotalSize));
            }
        }
        
        _lastContrastProfile = null;

        // Hack per forzare il refresh property changed se l'indice non cambia
        int tempIndex = CurrentIndex;
        CurrentIndex = -1; 
        CurrentIndex = tempIndex;
    }
    
    public override Task<List<string>> PrepareInputPathsAsync(IFitsIoService ioService)
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
            var data = await _ioService.LoadAsync(_multiModel.ImagePaths[index]);
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
    
    // --- COMANDO ANIMAZIONE ---

    [RelayCommand]
    public void ToggleAnimation()
    {
        if (IsAnimating) StopAnimation(); else StartAnimation();
    }

    private void StartAnimation()
    {
        if (_imageCount < 2) return;
        IsAnimating = true;
        _ = AnimationLoopAsync();
    }

    private void StopAnimation()
    {
        IsAnimating = false;
    }

    private async Task AnimationLoopAsync()
    {
        try
        {
            while (IsAnimating)
            {
                int nextIndex = (_currentIndex + 1) % _imageCount;
                
                // Carichiamo l'immagine direttamente (senza passare dalla proprietà CurrentIndex
                // per avere un controllo più fine sul timing)
                await LoadImageAtIndexAsync(nextIndex);
                
                // Aggiorniamo la proprietà solo alla fine per muovere lo slider UI
                SetProperty(ref _currentIndex, nextIndex, nameof(CurrentIndex));
                OnPropertyChanged(nameof(CurrentImageText));

                await Task.Delay(AnimationDelayMs);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Animation Error: {ex.Message}");
            StopAnimation();
        }
        finally
        {
            IsAnimating = false; 
            OnPropertyChanged(nameof(CanShowPrevious));
            OnPropertyChanged(nameof(CanShowNext));
        }
    }
    
    public override async Task RefreshDataFromDiskAsync()
    {
        _dataCache.Clear();
        await LoadImageAtIndexAsync(CurrentIndex);
    }

    // --- Dispose ---
    
    // Flag privato per evitare problemi di doppia dispose
    private bool _isDisposed;

    protected override void Dispose(bool disposing)
    {
        if (!_isDisposed)
        {
            if (disposing)
            {
                StopAnimation();
                _loadingCts?.Cancel();
                
                ActiveFitsImage?.UnloadData();
                _dataCache.Clear(); 
            }
            
            // Pulizia cartella temporanea
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
            
            _isDisposed = true;
        }
        
        base.Dispose(disposing);
    }
}