using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kometra.Infrastructure; // Aggiunto per localizzazione
using Kometra.Models.Export;
using Kometra.Models.Processing.Batch;
using Kometra.Models.Visualization;
using Kometra.Services.Factories;
using Kometra.Services.Fits;
using Kometra.Services.ImportExport;
using Kometra.Services.Settings; // Aggiunto per IToolParametersCache
using Kometra.Services.UI;
using Kometra.ViewModels.Shared;
using Kometra.ViewModels.Visualization;

namespace Kometra.ViewModels.ImportExport;

public partial class ExportViewModel : ObservableObject, IDisposable
{
    private readonly IExportCoordinator _coordinator;
    private readonly IDialogService _dialogService;
    private readonly IFitsDataManager _dataManager;
    private readonly IFitsRendererFactory _rendererFactory;
    private readonly IToolParametersCache _parametersCache; // Aggiunto cassetto

    private CancellationTokenSource? _exportCts;
    private CancellationTokenSource? _previewCts;
    
    private List<ExportableItem> _navigableItems = new();
    private bool _isSyncing; 

    public ObservableCollection<ExportableItem> Items { get; } = new();
    public SequenceNavigator Navigator { get; } = new();
    public EnhancementImageViewport Viewport { get; } = new();

    public TaskCompletionSource<bool> ImageLoadedTcs { get; private set; } = new();

    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(CurrentBlackPoint))]
    [NotifyPropertyChangedFor(nameof(CurrentWhitePoint))]
    [NotifyPropertyChangedFor(nameof(IsNavigationUIVisible))] // Notifica la visibilità della navigazione
    private FitsRenderer? _activeRenderer;
    
    [ObservableProperty] private double _dataMin = 0;
    [ObservableProperty] private double _dataMax = 65535;

    [ObservableProperty] private ExportableItem? _selectedPreviewItem;
    [ObservableProperty] private bool _isLoadingPreview;
    
    public string CurrentImageText 
    {
        get 
        {
            if (SelectedPreviewItem != null && _navigableItems.Contains(SelectedPreviewItem))
            {
                int idx = _navigableItems.IndexOf(SelectedPreviewItem) + 1;
                return $"{idx} / {_navigableItems.Count}";
            }
            return "- / -";
        }
    }

    public List<ExportFormat> AvailableFormats { get; } = Enum.GetValues<ExportFormat>().ToList();
    public List<FitsCompressionMode> AvailableCompressions { get; } = Enum.GetValues<FitsCompressionMode>().ToList();
    
    // Lista per popolare la ComboBox dello Stretch ed evitare che risulti vuota all'avvio
    public ObservableCollection<string> StretchModes { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFitsOptionsVisible))]
    [NotifyPropertyChangedFor(nameof(IsJpegOptionsVisible))]
    [NotifyPropertyChangedFor(nameof(IsVisualProcessingVisible))]
    [NotifyPropertyChangedFor(nameof(CanMergeToMef))] 
    private ExportFormat _selectedFormat = ExportFormat.FITS;

    [ObservableProperty] private bool _mergeIntoSingleFile;
    [ObservableProperty] private FitsCompressionMode _selectedCompression = FitsCompressionMode.None; 
    [ObservableProperty] private int _jpegQuality = 95;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsManualStretch))]
    private string _stretchMode = string.Empty;

    public double CurrentBlackPoint
    {
        get => _activeRenderer?.BlackPoint ?? 0;
        set { if (_activeRenderer != null) { _activeRenderer.BlackPoint = value; OnPropertyChanged(); } }
    }

    public double CurrentWhitePoint
    {
        get => _activeRenderer?.WhitePoint ?? 0;
        set { if (_activeRenderer != null) { _activeRenderer.WhitePoint = value; OnPropertyChanged(); } }
    }

    [ObservableProperty] 
    [NotifyCanExecuteChangedFor(nameof(ConfirmExportCommand))]
    private string _outputDirectory = string.Empty;

    [ObservableProperty] private string _baseFileName = ""; 

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmExportCommand))]
    [NotifyCanExecuteChangedFor(nameof(SelectFolderCommand))]
    [NotifyPropertyChangedFor(nameof(IsInteractionEnabled))]
    private bool _isExporting;

    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private string _statusText = string.Empty;

    public event Action? RequestClose;

    // --- Computed Properties ---
    public bool IsInteractionEnabled => !IsExporting;
    public bool IsJpegOptionsVisible => SelectedFormat == ExportFormat.JPEG;
    public bool IsFitsOptionsVisible => SelectedFormat == ExportFormat.FITS;
    public bool IsVisualProcessingVisible => !IsFitsOptionsVisible;
    public bool IsManualStretch => StretchMode == LocalizationManager.Instance["ExportStretchManual"] && IsVisualProcessingVisible;

    // Visibile solo se è formato FITS E ci sono più di 1 file selezionati.
    public bool CanMergeToMef => IsFitsOptionsVisible && _navigableItems.Count > 1;

    // Proprietà per controllare la visibilità della navigazione nella Viewport
    public bool IsNavigationUIVisible => ActiveRenderer != null && _navigableItems.Count > 0;

    private bool CanInteract() => !IsExporting;

    public ExportViewModel(
        IExportCoordinator coordinator,
        IDialogService dialogService,
        IFitsDataManager dataManager,
        IFitsRendererFactory rendererFactory,
        IToolParametersCache parametersCache, // Aggiunto nel costruttore
        IEnumerable<string> sourceFilePaths)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _dataManager = dataManager;
        _rendererFactory = rendererFactory;
        _parametersCache = parametersCache;

        // Popolamento delle modalità di stretch per la ComboBox
        StretchModes.Add(LocalizationManager.Instance["ExportStretchAuto"]);
        StretchModes.Add(LocalizationManager.Instance["ExportStretchManual"]);
        
        // --- LETTURA DALLA CACHE ---
        var settings = _parametersCache.Export;
        
        SelectedFormat = settings.SelectedFormat;
        MergeIntoSingleFile = settings.MergeIntoSingleFile;
        SelectedCompression = settings.SelectedCompression;
        JpegQuality = settings.JpegQuality;

        // Se abbiamo uno stretch salvato e corrisponde a uno esistente, usiamolo, altrimenti default
        if (!string.IsNullOrEmpty(settings.StretchMode) && StretchModes.Contains(settings.StretchMode))
            _stretchMode = settings.StretchMode;
        else
            _stretchMode = StretchModes[0];

        // Se la cartella in cache è valida usiamola, altrimenti Documents
        if (!string.IsNullOrWhiteSpace(settings.OutputDirectory))
            OutputDirectory = settings.OutputDirectory;
        else
            OutputDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        foreach (var path in sourceFilePaths)
        {
            var item = new ExportableItem(path);
            item.PropertyChanged += OnItemPropertyChanged;
            Items.Add(item);
        }

        RefreshNavigableItems();
        Navigator.IndexChanged += OnNavigatorIndexChanged;

        if (Items.Count > 0)
        {
            SelectedPreviewItem = Items[0];
        }
    }

    [RelayCommand]
    private async Task ResetThresholds()
    {
        if (_activeRenderer == null) return;

        // 1. Chiamiamo direttamente il reset delle soglie
        await _activeRenderer.ResetThresholdsAsync();
    
        // 2. Notifichiamo all'interfaccia che i valori sono cambiati
        // in modo che gli slider si aggiornino visivamente
        OnPropertyChanged(nameof(CurrentBlackPoint));
        OnPropertyChanged(nameof(CurrentWhitePoint));
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ExportableItem.IsSelected))
        {
            RefreshNavigableItems();
            UpdateSelectionCommandsState();
        }
    }

    private void RefreshNavigableItems()
    {
        _navigableItems = Items.Where(i => i.IsSelected).ToList();
        Navigator.UpdateStatus(0, _navigableItems.Count);

        // Se abbiamo 1 o 0 file selezionati, disabilitiamo il Merge e notifichiamo la UI
        if (_navigableItems.Count <= 1)
        {
            MergeIntoSingleFile = false;
        }
        OnPropertyChanged(nameof(CanMergeToMef)); 
        OnPropertyChanged(nameof(IsNavigationUIVisible)); // Notifica il cambio di stato visibilità navigazione

        if (SelectedPreviewItem != null && _navigableItems.Contains(SelectedPreviewItem))
        {
            int idx = _navigableItems.IndexOf(SelectedPreviewItem);
            if (Navigator.CurrentIndex != idx) 
            {
                _isSyncing = true;
                Navigator.CurrentIndex = idx;
                _isSyncing = false;
            }
        }
        
        OnPropertyChanged(nameof(CurrentImageText));
        ConfirmExportCommand.NotifyCanExecuteChanged();
    }

    private void UpdateSelectionCommandsState()
    {
        SelectAllCommand.NotifyCanExecuteChanged();
        DeselectAllCommand.NotifyCanExecuteChanged();
    }

    private async void OnNavigatorIndexChanged(object? sender, int index)
    {
        if (_isSyncing) return;
        
        if (index >= 0 && index < _navigableItems.Count)
        {
            var item = _navigableItems[index];
            _isSyncing = true;
            SelectedPreviewItem = item;
            _isSyncing = false;
            OnPropertyChanged(nameof(CurrentImageText));
            await LoadImageAsync(item);
        }
    }

    partial void OnSelectedPreviewItemChanged(ExportableItem? value)
    {
        if (_isSyncing || value == null) return;
        _ = LoadImageAsync(value);
        if (_navigableItems.Contains(value))
        {
            int idx = _navigableItems.IndexOf(value);
            if (Navigator.CurrentIndex != idx)
            {
                _isSyncing = true;
                Navigator.CurrentIndex = idx;
                _isSyncing = false;
            }
        }
        OnPropertyChanged(nameof(CurrentImageText));
    }

    private async Task LoadImageAsync(ExportableItem item)
    {
        if (ImageLoadedTcs.Task.IsCompleted) ImageLoadedTcs = new TaskCompletionSource<bool>();

        _previewCts?.Cancel();
        _previewCts = new CancellationTokenSource();
        var token = _previewCts.Token;

        IsLoadingPreview = true;
        try
        {
            var fitsData = await _dataManager.GetDataAsync(item.FullPath);
            var imageHdu = fitsData.FirstImageHdu ?? fitsData.PrimaryHdu;
            
            if (imageHdu == null) return;
            if (token.IsCancellationRequested) return;

            // --- NUOVO: Calcolo asincrono dei limiti minimi e massimi ---
            var (min, max) = await Task.Run(() => CalculateMinMax(imageHdu.PixelData as Array), token);
            if (token.IsCancellationRequested) return;

            DataMin = min;
            DataMax = max;
            // -------------------------------------------------------------

            var newRenderer = await _rendererFactory.CreateAsync(imageHdu.PixelData, imageHdu.Header);

            if (token.IsCancellationRequested) { newRenderer.Dispose(); return; }

            if (_activeRenderer != null)
                newRenderer.ApplyRelativeProfile(_activeRenderer.CaptureSigmaProfile());
            else
                await newRenderer.ResetThresholdsAsync();

            _activeRenderer?.Dispose();
            ActiveRenderer = newRenderer;
            Viewport.ImageSize = ActiveRenderer.ImageSize;

            OnPropertyChanged(nameof(CurrentBlackPoint));
            OnPropertyChanged(nameof(CurrentWhitePoint));

            if (!ImageLoadedTcs.Task.IsCompleted) ImageLoadedTcs.TrySetResult(true);
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                StatusText = string.Format(LocalizationManager.Instance["ErrorGeneric"], ex.Message);
                if (!ImageLoadedTcs.Task.IsCompleted) ImageLoadedTcs.TrySetException(ex);
            }
        }
        finally
        {
            if (!token.IsCancellationRequested) IsLoadingPreview = false;
        }
    }

    // --- Metodo di supporto per calcolare Min e Max velocemente ---
    private static (double Min, double Max) CalculateMinMax(Array? pixelData)
    {
        if (pixelData == null || pixelData.Length == 0) return (0, 65535);

        double min = double.MaxValue;
        double max = double.MinValue;

        // Ottimizzazione per i tipi FITS più comuni (evita conversioni lente)
        if (pixelData is float[] floatArray)
        {
            for (int i = 0; i < floatArray.Length; i++) 
            { 
                if (floatArray[i] < min) min = floatArray[i]; 
                if (floatArray[i] > max) max = floatArray[i]; 
            }
        }
        else if (pixelData is ushort[] ushortArray) // FITS 16-bit unsigned
        {
            for (int i = 0; i < ushortArray.Length; i++) 
            { 
                if (ushortArray[i] < min) min = ushortArray[i]; 
                if (ushortArray[i] > max) max = ushortArray[i]; 
            }
        }
        else if (pixelData is short[] shortArray) // FITS 16-bit signed
        {
            for (int i = 0; i < shortArray.Length; i++) 
            { 
                if (shortArray[i] < min) min = shortArray[i]; 
                if (shortArray[i] > max) max = shortArray[i]; 
            }
        }
        else if (pixelData is double[] doubleArray)
        {
            for (int i = 0; i < doubleArray.Length; i++) 
            { 
                if (doubleArray[i] < min) min = doubleArray[i]; 
                if (doubleArray[i] > max) max = doubleArray[i]; 
            }
        }
        else
        {
            // Fallback per matrici multidimensionali o tipi non previsti
            foreach (var p in pixelData)
            {
                double val = Convert.ToDouble(p);
                if (val < min) min = val;
                if (val > max) max = val;
            }
        }

        // Protezione per immagini corrotte o completamente nere/bianche
        if (min == double.MaxValue || max == double.MinValue) return (0, 65535);
        if (min == max) max = min + 1.0; // Evita che lo slider abbia range zero

        return (min, max);
    }

    async partial void OnStretchModeChanged(string? value) // Aggiungi il '?' per accettare null
    {
        // Esci se il valore è null
        if (_activeRenderer == null || string.IsNullOrEmpty(value)) return; 
    
        if (value.Contains(LocalizationManager.Instance["ExportStretchAuto"])) 
            await _activeRenderer.ResetThresholdsAsync();
    
        OnPropertyChanged(nameof(CurrentBlackPoint));
        OnPropertyChanged(nameof(CurrentWhitePoint));
    }

    [RelayCommand(CanExecute = nameof(CanInteract))]
    private async Task SelectFolder()
    {
        var path = await _dialogService.ShowOpenFolderDialogAsync(LocalizationManager.Instance["ExportSelectFolderTitle"]);
        if (!string.IsNullOrWhiteSpace(path)) OutputDirectory = path;
    }

    private bool CanExport() => !IsExporting && !string.IsNullOrWhiteSpace(OutputDirectory) && _navigableItems.Count > 0;

    /// <summary>
    /// Calcola l'estensione corretta in base al formato e alla compressione.
    /// Importante per distinguere .fits (standard) da .fits.fz (compresso).
    /// </summary>
    private string GetCorrectExtension()
    {
        switch (SelectedFormat)
        {
            case ExportFormat.JPEG: return ".jpg";
            case ExportFormat.PNG: return ".png";
            case ExportFormat.FITS:
                return SelectedCompression != FitsCompressionMode.None ? ".fits.fz" : ".fits";
            default: return ".dat";
        }
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ConfirmExport()
    {
        IsExporting = true;
        StatusText = LocalizationManager.Instance["StatusInit"];
        ProgressValue = 0;
        _exportCts = new CancellationTokenSource();

        // --- SALVATAGGIO IN CACHE ---
        var settings = _parametersCache.Export;
        settings.SelectedFormat = SelectedFormat;
        settings.MergeIntoSingleFile = MergeIntoSingleFile;
        settings.SelectedCompression = SelectedCompression;
        settings.JpegQuality = JpegQuality;
        settings.StretchMode = StretchMode;
        settings.OutputDirectory = OutputDirectory;

        foreach (var item in _navigableItems)
        {
            item.Status = LocalizationManager.Instance["ExportStatusQueued"];
            item.IsSuccess = false; item.IsError = false;
        }

        try
        {
            // 1. Pulizia Nome File Input
            string cleanedFileName = BaseFileName;
            if (!string.IsNullOrWhiteSpace(cleanedFileName))
            {
                // Rimuove estensioni errate se l'utente le ha digitate manualmente
                if (cleanedFileName.EndsWith(".fits", StringComparison.OrdinalIgnoreCase))
                    cleanedFileName = cleanedFileName.Substring(0, cleanedFileName.Length - 5);
                else if (cleanedFileName.EndsWith(".fit", StringComparison.OrdinalIgnoreCase))
                    cleanedFileName = cleanedFileName.Substring(0, cleanedFileName.Length - 4);
                else if (cleanedFileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || 
                         cleanedFileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                    cleanedFileName = cleanedFileName.Substring(0, cleanedFileName.Length - 4);
            }

            // 2. Preparazione Profilo
            ContrastProfile profile;
            if (IsFitsOptionsVisible) profile = new AbsoluteContrastProfile(0, 65535);
            else if (_activeRenderer != null)
            {
                if (IsManualStretch) profile = _activeRenderer.CaptureContrastProfile();
                else profile = _activeRenderer.CaptureSigmaProfile();
            }
            else profile = new AbsoluteContrastProfile(0, 65535);

            // 3. Creazione Job Settings
            var settingsJob = new ExportJobSettings(
                OutputDirectory, 
                cleanedFileName, // Nome pulito
                SelectedFormat, 
                MergeIntoSingleFile,
                SelectedCompression, 
                JpegQuality, 
                profile);

            var progress = new Progress<BatchProgressReport>(r =>
            {
                ProgressValue = r.Percentage;
                StatusText = $"{r.CurrentFileName} ({r.CurrentFileIndex}/{r.TotalFiles})";
            });

            await _coordinator.ExecuteExportAsync(_navigableItems, settingsJob, progress, _exportCts.Token);

            StatusText = LocalizationManager.Instance["ExportStatusDone"];
            ProgressValue = 100;
            await Task.Delay(1500);
            RequestClose?.Invoke();
        }
        catch (OperationCanceledException) { StatusText = LocalizationManager.Instance["StatusCancelledByUser"]; }
        catch (Exception ex) { StatusText = string.Format(LocalizationManager.Instance["ErrorGeneric"], ex.Message); }
        finally
        {
            IsExporting = false;
            _exportCts?.Dispose();
            _exportCts = null;
        }
    }
    
    [RelayCommand(CanExecute = nameof(CanSelectAll))]
    private void SelectAll() => SetSelectionInternal(true);
    private bool CanSelectAll() => Items.Any(i => !i.IsSelected);

    [RelayCommand(CanExecute = nameof(CanDeselectAll))]
    private void DeselectAll() => SetSelectionInternal(false);
    private bool CanDeselectAll() => Items.Any(i => i.IsSelected);

    private void SetSelectionInternal(bool select)
    {
        foreach (var item in Items) item.PropertyChanged -= OnItemPropertyChanged;
        foreach (var item in Items) item.IsSelected = select;
        foreach (var item in Items) item.PropertyChanged += OnItemPropertyChanged;
        RefreshNavigableItems();
        UpdateSelectionCommandsState();
    }

    [RelayCommand]
    private void Cancel()
    {
        if (IsExporting) { _exportCts?.Cancel(); StatusText = LocalizationManager.Instance["StatusCancelling"]; }
        else RequestClose?.Invoke();
    }

    public void Dispose()
    {
        _previewCts?.Dispose();
        _exportCts?.Dispose();
        foreach(var item in Items) item.PropertyChanged -= OnItemPropertyChanged;
        Navigator.IndexChanged -= OnNavigatorIndexChanged;
        _activeRenderer?.Dispose();
    }
}