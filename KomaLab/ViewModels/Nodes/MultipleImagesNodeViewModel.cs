using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KomaLab.Models.Fits;
using KomaLab.Models.Nodes;
using KomaLab.Models.Visualization;
using KomaLab.Services.Factories;
using KomaLab.Services.Fits;
using KomaLab.Services.Processing.Coordinators;
using KomaLab.ViewModels.Visualization;
using SequenceNavigator = KomaLab.ViewModels.Shared.SequenceNavigator;

namespace KomaLab.ViewModels.Nodes;

/// <summary>
/// Nodo per la gestione di sequenze di immagini FITS.
/// Adotta il pattern Async Factory per garantire che i Renderer siano sempre validi.
/// </summary>
public partial class MultipleImagesNodeViewModel : ImageNodeViewModel
{
    // --- Dipendenze ---
    private readonly IFitsDataManager _dataManager;
    private readonly IFitsRendererFactory _rendererFactory;
    private readonly IVideoExportCoordinator _videoCoordinator;

    // --- Componenti e Stato ---
    private readonly List<FitsFileReference> _files = new();
    private readonly SequenceNavigator _navigator = new();
    private readonly Size _maxImageSize;
    
    private CancellationTokenSource? _navigationCts;
    private CancellationTokenSource? _exportCts;

    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(ActiveRenderer))] 
    private FitsRenderer? _activeFitsImage;

    [ObservableProperty] private bool _isExporting;
    [ObservableProperty] private double _exportProgress;

    // ---------------------------------------------------------------------------
    // OVERRIDES CAPABILITY & LAYOUT
    // ---------------------------------------------------------------------------
    
    public override IImageNavigator Navigator => _navigator;
    
    // Binding Proxy: La view deve bindare a ActiveFitsImage per aggiornamenti sicuri
    public override FitsRenderer? ActiveRenderer => ActiveFitsImage;
    
    public override Size NodeContentSize => _maxImageSize;
    public override IReadOnlyList<FitsFileReference> CurrentFiles => _files;
    
    public override FitsFileReference? ActiveFile => 
        (_navigator.CurrentIndex >= 0 && _navigator.CurrentIndex < _files.Count) 
        ? _files[_navigator.CurrentIndex] 
        : null;

    public ObservableCollection<string> ImageNames { get; } = new();
    public string CurrentImageText => $"{_navigator.DisplayIndex} / {_files.Count}";
    
    public bool CanExport => !IsExporting && !_navigator.IsLooping && _files.Count > 0 && ActiveRenderer != null;

    // ---------------------------------------------------------------------------
    // COSTRUTTORE E INIZIALIZZAZIONE
    // ---------------------------------------------------------------------------

    public MultipleImagesNodeViewModel(
        MultipleImagesNodeModel model,
        IFitsDataManager dataManager,
        IFitsRendererFactory rendererFactory,
        IVideoExportCoordinator videoCoordinator,
        Size maxSize) 
        : base(model)
    {
        _dataManager = dataManager ?? throw new ArgumentNullException(nameof(dataManager));
        _rendererFactory = rendererFactory ?? throw new ArgumentNullException(nameof(rendererFactory));
        _videoCoordinator = videoCoordinator ?? throw new ArgumentNullException(nameof(videoCoordinator));
        _maxImageSize = maxSize;

        foreach (var path in model.ImagePaths)
        {
            _files.Add(new FitsFileReference(path));
            ImageNames.Add(Path.GetFileName(path));
        }

        // Configurazione Navigatore
        _navigator.UpdateStatus(0, _files.Count);
        _navigator.IndexChanged += OnNavigatorIndexChanged;
    }

    public async Task InitializeAsync()
    {
        if (_files.Count == 0) return;
        await LoadImageAtIndexAsync(0, forceReset: true);
    }

    private async void OnNavigatorIndexChanged(object? sender, int index)
    {
        OnPropertyChanged(nameof(CurrentImageText));
        OnPropertyChanged(nameof(ActiveFile));
        ExportVideoCommand.NotifyCanExecuteChanged();
        
        await LoadImageAtIndexAsync(index);
    }

    // ---------------------------------------------------------------------------
    // CORE RENDERING PIPELINE (Async Factory Pattern)
    // ---------------------------------------------------------------------------

    private async Task LoadImageAtIndexAsync(int index, bool forceReset = false)
    {
        if (index < 0 || index >= _files.Count) return;

        _navigationCts?.Cancel();
        _navigationCts?.Dispose();
        _navigationCts = new CancellationTokenSource();
        var token = _navigationCts.Token;

        try
        {
            var fileRef = _files[index];
            var data = await _dataManager.GetDataAsync(fileRef.FilePath);
            token.ThrowIfCancellationRequested();

            // 1. CREAZIONE & INIZIALIZZAZIONE ATOMICA
            // La Factory ora restituisce un oggetto Task<FitsRenderer> già "pronto all'uso".
            // Non servono più chiamate manuali a InitializeAsync().
            var newRenderer = await _rendererFactory.CreateAsync(
                data.PixelData, 
                fileRef.ModifiedHeader ?? data.Header
            );
            
            // 2. LOGICA ADATTIVA (Flicker-Free)
            AbsoluteContrastProfile? profile = null;
            if (!forceReset && ActiveFitsImage != null)
            {
                // Sicuro: CaptureScientificMat() non fallisce mai qui perché newRenderer è garantito valido.
                using var nextMat = newRenderer.CaptureScientificMat();
                token.ThrowIfCancellationRequested();
                
                profile = ActiveFitsImage.GetAdaptedProfileFor(nextMat);
            }

            // 3. SWAP ATOMICO
            await ApplyNewRendererAsync(newRenderer, profile);

            // 4. PREFETCH
            if (!token.IsCancellationRequested && !_navigator.IsLooping)
            {
                _ = PrefetchImageAsync(index + 1, token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) 
        { 
            System.Diagnostics.Debug.WriteLine($"[MultipleImagesNode] Load Error: {ex.Message}"); 
        }
    }

    private async Task PrefetchImageAsync(int nextIndex, CancellationToken token)
    {
        if (_files.Count == 0) return;
        int idx = nextIndex % _files.Count;
        try 
        { 
            await Task.Run(async () => 
            {
                if (token.IsCancellationRequested) return;
                await _dataManager.GetDataAsync(_files[idx].FilePath);
            }, token);
        } 
        catch { /* Ignora errori prefetch */ }
    }

    // ---------------------------------------------------------------------------
    // VIDEO EXPORT
    // ---------------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanExport))]
    public async Task ExportVideoAsync(string outputPath)
    {
        if (ActiveRenderer == null || _files.Count == 0) return;

        IsExporting = true;
        ExportProgress = 0;
        _exportCts = new CancellationTokenSource();

        try
        {
            await _videoCoordinator.ExportVideoAsync(
                _files, 
                outputPath, 
                24.0, 
                ActiveRenderer.CaptureContrastProfile(), 
                this.VisualizationMode,
                adaptiveStretch: true,
                token: _exportCts.Token);
        }
        catch (OperationCanceledException) { }
        finally
        {
            IsExporting = false;
            _exportCts?.Dispose();
            ExportVideoCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    public void CancelExport() => _exportCts?.Cancel();

    // ---------------------------------------------------------------------------
    // OVERRIDES E CLEANUP
    // ---------------------------------------------------------------------------

    protected override void OnRendererSwapping(FitsRenderer newRenderer)
    {
        // Questo setter generato da [ObservableProperty] scatena:
        // 1. PropertyChanged("ActiveFitsImage")
        // 2. PropertyChanged("ActiveRenderer") -> tramite [NotifyPropertyChangedFor]
        ActiveFitsImage = newRenderer;
    }

    public override async Task LoadInputAsync(IEnumerable<FitsFileReference> input)
    {
        _navigator.Stop();
        _files.Clear(); 
        ImageNames.Clear();
        
        foreach (var f in input) 
        { 
            _files.Add(f); 
            ImageNames.Add(Path.GetFileName(f.FilePath)); 
        }
        
        _navigator.UpdateStatus(0, _files.Count);
        await LoadImageAtIndexAsync(0, forceReset: true);
    }

    public override async Task RefreshDataFromDiskAsync()
    {
        if (ActiveFile != null) 
        { 
            _dataManager.Invalidate(ActiveFile.FilePath); 
            await LoadImageAtIndexAsync(_navigator.CurrentIndex); 
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _navigator.Stop(); 
            _navigator.IndexChanged -= OnNavigatorIndexChanged;
            _navigationCts?.Cancel(); _navigationCts?.Dispose();
            _exportCts?.Cancel(); _exportCts?.Dispose();
            ActiveFitsImage?.Dispose();
        }
        base.Dispose(disposing);
    }
}