using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KomaLab.Models.Export;
using KomaLab.Models.Processing;
using KomaLab.Models.Visualization;
using KomaLab.Services.Factories;
using KomaLab.Services.Fits;
using KomaLab.Services.ImportExport;
using KomaLab.Services.UI;
using KomaLab.ViewModels.Shared;
using KomaLab.ViewModels.Visualization;

namespace KomaLab.ViewModels.ImportExport;

public partial class ExportViewModel : ObservableObject, IDisposable
{
    private readonly IExportCoordinator _coordinator;
    private readonly IDialogService _dialogService;
    private readonly IFitsDataManager _dataManager;
    private readonly IFitsRendererFactory _rendererFactory;

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
    private FitsRenderer? _activeRenderer;

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFitsOptionsVisible))]
    [NotifyPropertyChangedFor(nameof(IsJpegOptionsVisible))]
    [NotifyPropertyChangedFor(nameof(IsVisualProcessingVisible))]
    [NotifyPropertyChangedFor(nameof(CanMergeToMef))] // Notifica anche la nuova proprietà
    private ExportFormat _selectedFormat = ExportFormat.Fits;

    [ObservableProperty] private bool _mergeIntoSingleFile;
    [ObservableProperty] private FitsCompressionMode _selectedCompression = FitsCompressionMode.None; 
    [ObservableProperty] private int _jpegQuality = 95;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsManualStretch))]
    private string _stretchMode = "Auto Stretch";

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

    // Computed Properties
    public bool IsInteractionEnabled => !IsExporting;
    
    // CORREZIONE: Solo JPEG mostra lo slider qualità (PNG è lossless)
    public bool IsJpegOptionsVisible => SelectedFormat == ExportFormat.Jpeg;
    
    public bool IsFitsOptionsVisible => SelectedFormat == ExportFormat.Fits;
    public bool IsVisualProcessingVisible => !IsFitsOptionsVisible;
    public bool IsManualStretch => StretchMode == "Manuale" && IsVisualProcessingVisible;

    // NUOVA PROPRIETÀ: Determina se l'opzione MEF deve essere visibile
    // Visibile solo se è formato FITS E ci sono più di 1 file selezionati.
    public bool CanMergeToMef => IsFitsOptionsVisible && _navigableItems.Count > 1;

    private bool CanInteract() => !IsExporting;

    public ExportViewModel(
        IExportCoordinator coordinator,
        IDialogService dialogService,
        IFitsDataManager dataManager,
        IFitsRendererFactory rendererFactory,
        IEnumerable<string> sourceFilePaths)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _dataManager = dataManager;
        _rendererFactory = rendererFactory;

        foreach (var path in sourceFilePaths)
        {
            var item = new ExportableItem(path);
            item.PropertyChanged += OnItemPropertyChanged;
            Items.Add(item);
        }

        OutputDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

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
        StretchMode = "Auto Stretch (Consigliato)";
        await _activeRenderer.ResetThresholdsAsync();
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
        OnPropertyChanged(nameof(CanMergeToMef)); // Aggiorna la visibilità del CheckBox

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
                StatusText = "Errore: " + ex.Message;
                if (!ImageLoadedTcs.Task.IsCompleted) ImageLoadedTcs.TrySetException(ex);
            }
        }
        finally
        {
            if (!token.IsCancellationRequested) IsLoadingPreview = false;
        }
    }

    async partial void OnStretchModeChanged(string value)
    {
        if (_activeRenderer == null) return;
        if (value.Contains("Auto")) await _activeRenderer.ResetThresholdsAsync();
        
        OnPropertyChanged(nameof(CurrentBlackPoint));
        OnPropertyChanged(nameof(CurrentWhitePoint));
    }

    [RelayCommand(CanExecute = nameof(CanInteract))]
    private async Task SelectFolder()
    {
        var path = await _dialogService.ShowOpenFolderDialogAsync("Seleziona destinazione");
        if (!string.IsNullOrWhiteSpace(path)) OutputDirectory = path;
    }

    private bool CanExport() => !IsExporting && !string.IsNullOrWhiteSpace(OutputDirectory) && _navigableItems.Count > 0;

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ConfirmExport()
    {
        IsExporting = true;
        StatusText = "Inizializzazione...";
        ProgressValue = 0;
        _exportCts = new CancellationTokenSource();

        foreach (var item in _navigableItems)
        {
            item.Status = "In coda";
            item.IsSuccess = false; item.IsError = false;
        }

        try
        {
            ContrastProfile profile;
            if (IsFitsOptionsVisible) profile = new AbsoluteContrastProfile(0, 65535);
            else if (_activeRenderer != null)
            {
                if (IsManualStretch) profile = _activeRenderer.CaptureContrastProfile();
                else profile = _activeRenderer.CaptureSigmaProfile();
            }
            else profile = new AbsoluteContrastProfile(0, 65535);

            var settings = new ExportJobSettings(
                OutputDirectory, BaseFileName, SelectedFormat, MergeIntoSingleFile,
                SelectedCompression, JpegQuality, profile);

            var progress = new Progress<BatchProgressReport>(r =>
            {
                ProgressValue = r.Percentage;
                StatusText = $"{r.CurrentFileName} ({r.CurrentFileIndex}/{r.TotalFiles})";
            });

            await _coordinator.ExecuteExportAsync(_navigableItems, settings, progress, _exportCts.Token);

            StatusText = "Esportazione completata.";
            ProgressValue = 100;
            await Task.Delay(1500);
            RequestClose?.Invoke();
        }
        catch (OperationCanceledException) { StatusText = "Annullato dall'utente."; }
        catch (Exception ex) { StatusText = $"Errore: {ex.Message}"; }
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
        if (IsExporting) { _exportCts?.Cancel(); StatusText = "Annullamento..."; }
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