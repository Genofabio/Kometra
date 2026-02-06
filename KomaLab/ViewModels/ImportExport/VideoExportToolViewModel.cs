using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KomaLab.Infrastructure;
using KomaLab.Models.Export;
using KomaLab.Models.Fits;
using KomaLab.Models.Visualization;
using KomaLab.Services.Factories;
using KomaLab.Services.Fits;
using KomaLab.Services.ImportExport;
using KomaLab.Services.UI;
using KomaLab.Services.Processing.Coordinators;
using KomaLab.ViewModels.Shared;
using KomaLab.ViewModels.Visualization;

namespace KomaLab.ViewModels.ImportExport;

public partial class VideoExportToolViewModel : ObservableObject, IDisposable
{
    // --- DIPENDENZE ---
    private readonly IVideoFormatProvider _formatProvider;
    private readonly IFitsDataManager _dataManager;
    private readonly IFitsRendererFactory _rendererFactory;
    private readonly IVideoExportCoordinator _videoCoordinator;
    private readonly IDialogService _dialogService;
    private readonly IReadOnlyList<FitsFileReference> _sourceFiles;

    // --- STATO E LOGICA ---
    private CancellationTokenSource? _exportCts;
    public event Action? RequestClose;
    public bool DialogResult { get; private set; }
    public TaskCompletionSource<bool> ImageLoadedTcs { get; private set; } = new();

    // --- VIEWPORT E NAVIGAZIONE ---
    public SequenceNavigator Navigator { get; } = new();
    public EnhancementImageViewport Viewport { get; } = new();
    
    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(CurrentImageText))]
    [NotifyPropertyChangedFor(nameof(CurrentBlackPoint))]
    [NotifyPropertyChangedFor(nameof(CurrentWhitePoint))]
    private FitsRenderer? _activeRenderer;

    // --- STATO ESPORTAZIONE (UI) ---
    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(IsInteractionEnabled))]
    private bool _isExporting;

    [ObservableProperty] private double _exportProgress;
    [ObservableProperty] private string _statusText = "Pronto";
    [ObservableProperty] 
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private string _outputPath = string.Empty;

    public bool IsInteractionEnabled => !IsExporting;
    public double CurrentBlackPoint => ActiveRenderer?.BlackPoint ?? 0;
    public double CurrentWhitePoint => ActiveRenderer?.WhitePoint ?? 0;
    public string CurrentImageText => $"{Navigator.DisplayIndex} / {Navigator.TotalCount}";

    // --- PARAMETRI VIDEO ---
    [ObservableProperty] private int _originalWidth;
    [ObservableProperty] private int _originalHeight;
    public int FinalWidth => (int)(OriginalWidth * ScaleFactor) & ~1;
    public int FinalHeight => (int)(OriginalHeight * ScaleFactor) & ~1;
    public string DurationText => Fps <= 0 ? "0s" : TimeSpan.FromSeconds((double)_sourceFiles.Count / Fps).ToString(@"mm\:ss") + "s";

    [ObservableProperty] private VideoContainer _selectedContainer;
    [ObservableProperty] private VideoCodec _selectedCodec;
    public ObservableCollection<VideoContainer> Containers { get; }
    [ObservableProperty] private ObservableCollection<VideoCodec> _availableCodecs;
    
    [ObservableProperty] private double _fps = 24.0;
    [ObservableProperty] private double _scaleFactor = 1.0; 
    [ObservableProperty] private VisualizationMode _mode;

    public VideoExportToolViewModel(
        IVideoFormatProvider formatProvider, 
        IFitsDataManager dataManager,
        IFitsRendererFactory rendererFactory,
        IVideoExportCoordinator videoCoordinator,
        IDialogService dialogService,
        IReadOnlyList<FitsFileReference> files,
        VisualizationMode currentMode, 
        Size originalSize)
    {
        _formatProvider = formatProvider;
        _dataManager = dataManager;
        _rendererFactory = rendererFactory;
        _videoCoordinator = videoCoordinator;
        _dialogService = dialogService;
        _sourceFiles = files;
        _mode = currentMode;
        
        _originalWidth = (int)originalSize.Width;
        _originalHeight = (int)originalSize.Height;

        var supported = _formatProvider.GetSupportedContainers().ToList();
        Containers = new ObservableCollection<VideoContainer>(supported);
        SelectedContainer = supported.Contains(VideoContainer.MP4) ? VideoContainer.MP4 : supported.FirstOrDefault();
        
        _availableCodecs = new ObservableCollection<VideoCodec>();
        UpdateCodecs();

        Navigator.UpdateStatus(0, _sourceFiles.Count);
        Navigator.IndexChanged += OnNavigatorIndexChanged;

        OnPropertyChanged(nameof(FinalWidth));
        OnPropertyChanged(nameof(FinalHeight));
        OnPropertyChanged(nameof(DurationText));
        
        _ = LoadImageAsync(0);
    }

    private async void OnNavigatorIndexChanged(object? sender, int index) => await LoadImageAsync(index);

    private async Task LoadImageAsync(int index)
    {
        try
        {
            if (index < 0 || index >= _sourceFiles.Count) return;
            var fileRef = _sourceFiles[index];
            var data = await _dataManager.GetDataAsync(fileRef.FilePath);
        
            // [MODIFICA MEF] Accesso sicuro all'HDU immagine
            var imageHdu = data.FirstImageHdu ?? data.PrimaryHdu;
            if (imageHdu == null) return; // O loggare errore

            // Usiamo PixelData e Header dell'HDU
            var newRenderer = await _rendererFactory.CreateAsync(imageHdu.PixelData, fileRef.ModifiedHeader ?? imageHdu.Header);
            newRenderer.VisualizationMode = Mode;

            if (ActiveRenderer != null)
                newRenderer.ApplyRelativeProfile(ActiveRenderer.CaptureSigmaProfile());
            else 
                await newRenderer.ResetThresholdsAsync();

            ActiveRenderer?.Dispose();
            ActiveRenderer = newRenderer;
            Viewport.ImageSize = ActiveRenderer.ImageSize;
            NotifyThresholdsChanged();
        
            if (!ImageLoadedTcs.Task.IsCompleted) ImageLoadedTcs.TrySetResult(true);
        }
        catch (Exception ex)
        {
            if (!ImageLoadedTcs.Task.IsCompleted) ImageLoadedTcs.TrySetException(ex);
        }
    }

    private void UpdateCodecs()
    {
        var supported = _formatProvider.GetSupportedCodecs(SelectedContainer).ToList();
        AvailableCodecs = new ObservableCollection<VideoCodec>(supported);
        SelectedCodec = AvailableCodecs.FirstOrDefault();
        ConfirmCommand.NotifyCanExecuteChanged();
    }

    // --- COMANDI PRINCIPALI ---

    [RelayCommand]
    private async Task SelectPath()
    {
        string extension = _formatProvider.GetExtension(SelectedContainer);
        string rawName = _sourceFiles.FirstOrDefault()?.FileName ?? "VideoExport";
        string defaultFileName = rawName.Contains('.') ? rawName.Substring(0, rawName.IndexOf('.')) : rawName;

        var path = await _dialogService.ShowSaveFileDialogAsync(
            $"{defaultFileName}{extension}", 
            $"{SelectedContainer} Video", 
            $"*{extension}");

        if (!string.IsNullOrWhiteSpace(path)) OutputPath = path;
    }

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private async Task Confirm() 
    { 
        IsExporting = true;
        _exportCts = new CancellationTokenSource();

        try 
        {
            var settings = GetSettings(OutputPath);
            var progress = new Progress<double>(p => {
                ExportProgress = p;
                if (p >= 99) 
                    StatusText = "Finalizzazione e scrittura indici... Non chiudere.";
                else 
                    StatusText = $"Elaborazione frame... {p:N0}%";
            });

            // L'await qui sotto ora aspetterà anche la "Barriera di Stabilità"
            await _videoCoordinator.ExportVideoAsync(
                _sourceFiles, 
                settings, 
                settings.InitialProfile, 
                progress, 
                _exportCts.Token);

            // Ultimo check di sicurezza UI
            StatusText = "Video Salvato!";
            ExportProgress = 100;
            await Task.Delay(1000); // Mostra il successo per un secondo

            DialogResult = true; 
            RequestClose?.Invoke(); 
        }
        catch (OperationCanceledException) { StatusText = "Annullato."; }
        catch (Exception ex) { StatusText = "Errore.";  }
        finally 
        { 
            IsExporting = false;
            _exportCts?.Dispose();
            _exportCts = null;
        }
    }

    private bool CanConfirm() => !IsExporting && !string.IsNullOrWhiteSpace(OutputPath) && SelectedCodec != null;

    [RelayCommand] 
    private void Cancel() 
    { 
        if (IsExporting) 
        {
            _exportCts?.Cancel();
            StatusText = "Annullamento in corso...";
        }
        else 
        {
            DialogResult = false; 
            RequestClose?.Invoke(); 
        }
    }

    [RelayCommand] 
    private async Task ResetThresholds() 
    { 
        if (ActiveRenderer != null) { await ActiveRenderer.ResetThresholdsAsync(); NotifyThresholdsChanged(); }
    }

    public void NotifyThresholdsChanged()
    {
        OnPropertyChanged(nameof(CurrentBlackPoint));
        OnPropertyChanged(nameof(CurrentWhitePoint));
    }

    // --- LOGICA SETTINGS ---
    public VideoExportSettings GetSettings(string outputPath) 
    {
        var currentProfile = ActiveRenderer?.CaptureContrastProfile() ?? new AbsoluteContrastProfile(0, 65535);
        return new VideoExportSettings(outputPath, Fps, SelectedContainer, SelectedCodec, currentProfile, ScaleFactor, Mode, true);
    }

    partial void OnSelectedContainerChanged(VideoContainer value) => UpdateCodecs();
    partial void OnScaleFactorChanged(double value) { OnPropertyChanged(nameof(FinalWidth)); OnPropertyChanged(nameof(FinalHeight)); }
    partial void OnFpsChanged(double value) => OnPropertyChanged(nameof(DurationText));

    public void Dispose()
    {
        _exportCts?.Cancel();
        _exportCts?.Dispose();
        RequestClose = null;
        ActiveRenderer?.Dispose();
        Navigator.IndexChanged -= OnNavigatorIndexChanged;
    }
}