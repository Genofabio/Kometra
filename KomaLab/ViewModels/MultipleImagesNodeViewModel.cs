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
    private CancellationTokenSource? _loadingCts;
    private Size _maxImageSize;
    
    // Memorizza le preferenze di contrasto (Sigma/Assoluto) durante lo scorrimento
    private ContrastProfile? _lastContrastProfile;

    // --- Proprietà Observable ---

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveRenderer))] // Notifica la base che il renderer è cambiato
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
        if (!IsAnimating) 
        {
            _ = LoadImageAtIndexAsync(value);
        }
    }

    // --- Logica Caricamento e Swap Immagini ---
    private async Task LoadImageAtIndexAsync(int index)
    {
        if (index < 0 || index >= _imageCount) return;

        // --- FIX 1: ANNULLAMENTO PRECEDENTE ---
        // Se c'è un caricamento in corso, lo annulliamo.
        // Questo previene che 10 click lancino 10 processi paralleli.
        _loadingCts?.Cancel();
        _loadingCts = new CancellationTokenSource();
        var token = _loadingCts.Token;

        try
        {
            var cachedData = await GetOrLoadDataAtIndex(index);
            
            // Se siamo stati annullati nel frattempo (nuovo click), usciamo subito
            if (token.IsCancellationRequested) return;

            // Se l'immagine è la stessa (es. click veloci avanti/indietro), non fare nulla
            if (ActiveFitsImage != null && ActiveFitsImage.Data == cachedData) return;
            if (cachedData == null) return;

            ContrastProfile? profileToApply = _lastContrastProfile;

            // --- FIX 2: CATTURA SICURA ---
            // Catturiamo il profilo dall'immagine attuale MA NON LA SCARICHIAMO ANCORA.
            // Se la scaricassimo ora, la UI rimarrebbe nera o crasherebbe durante il caricamento della nuova.
            if (ActiveFitsImage != null && !ActiveFitsImage.IsDisposed) // Controllo sicurezza
            {
                profileToApply = ActiveFitsImage.CaptureContrastProfile();
                _lastContrastProfile = profileToApply;
            }

            // Creazione nuovo renderer (Operazione pesante)
            var newFitsImage = new FitsRenderer(cachedData, _fitsService, _converter, _analysis);
            
            // Inizializza (qui dentro avviene il lavoro pesante su thread separato)
            // Nota: Se FitsRenderer.InitializeAsync supportasse un CancellationToken sarebbe meglio,
            // ma per ora controlliamo il token subito dopo.
            await newFitsImage.InitializeAsync();

            // --- FIX 3: PUNTO DI NON RITORNO ---
            // Se l'utente ha cliccato un'altra volta mentre caricavamo, 
            // BUTTIAMO VIA il lavoro fatto (Dispose del nuovo) e non tocchiamo la UI.
            if (token.IsCancellationRequested)
            {
                newFitsImage.UnloadData();
                return;
            }

            // Applicazione Profilo
            if (profileToApply != null)
            {
                newFitsImage.ApplyContrastProfile(profileToApply);
            }

            // Aggiorna Viewport
            Viewport.ImageSize = newFitsImage.ImageSize;

            // --- FIX 4: SWAP ATOMICO E PULIZIA ---
            // Solo ora che siamo pronti e sicuri, scambiamo i renderer
            var oldImage = ActiveFitsImage;
            ActiveFitsImage = newFitsImage; // La UI si aggiorna qui

            // Ora possiamo distruggere quello vecchio in sicurezza
            oldImage?.UnloadData();

            // Aggiorna UI valori
            BlackPoint = newFitsImage.BlackPoint;
            WhitePoint = newFitsImage.WhitePoint;

            PreviousImageCommand.NotifyCanExecuteChanged();
            NextImageCommand.NotifyCanExecuteChanged();

            // Prefetch senza await (fire and forget sicuro)
            _ = PrefetchImageAsync(index + 1);
        }
        catch (OperationCanceledException)
        {
            // Normale durante click rapidi, ignoriamo
        }
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
    
    // --- COMANDO ANIMAZIONE ---

    [RelayCommand]
    public void ToggleAnimation()
    {
        if (IsAnimating)
        {
            StopAnimation();
        }
        else
        {
            StartAnimation();
        }
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
        // Il loop controlla questa variabile a ogni giro e si fermerà da solo
    }

    private async Task AnimationLoopAsync()
    {
        try
        {
            while (IsAnimating)
            {
                // 1. Calcola prossimo indice (Loop circolare)
                int nextIndex = (_currentIndex + 1) % _imageCount;

                // 2. Carica l'immagine (questo metodo gestisce già il rendering e l'SRP)
                // Usiamo await: il prossimo frame non parte finché questo non è finito!
                await LoadImageAtIndexAsync(nextIndex);
                
                SetProperty(ref _currentIndex, nextIndex, nameof(CurrentIndex));
                OnPropertyChanged(nameof(CurrentImageText)); // Aggiorna "1/10"

                // 3. Attesa per il frame rate
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
            // Assicuriamoci che lo stato sia coerente quando usciamo
            IsAnimating = false; 
            
            // Ripristina i pulsanti freccia
            OnPropertyChanged(nameof(CanShowPrevious));
            OnPropertyChanged(nameof(CanShowNext));
        }
    }
    
    public override async Task RefreshDataFromDiskAsync()
    {
        // 1. Svuota la cache interna (fondamentale!)
        _dataCache.Clear();

        // 2. Ricarica l'immagine attualmente visualizzata
        await LoadImageAtIndexAsync(CurrentIndex);
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