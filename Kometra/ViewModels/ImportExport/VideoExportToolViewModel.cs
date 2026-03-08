using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kometra.Services.Processing.Coordinators;
using Kometra.Infrastructure;
using Kometra.Models.Export;
using Kometra.Models.Fits;
using Kometra.Models.Visualization;
using Kometra.Services.Factories;
using Kometra.Services.Fits;
using Kometra.Services.ImportExport;
using Kometra.Services.UI;
using Kometra.ViewModels.Shared;
using Kometra.ViewModels.Visualization;

namespace Kometra.ViewModels.ImportExport;

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
    [ObservableProperty] private string _statusText = string.Empty;
    
    // --- GESTIONE PERCORSO E NOME FILE ---
    
    [ObservableProperty] 
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    [NotifyPropertyChangedFor(nameof(OutputPath))] // Notifica cambiamenti alla proprietà legacy
    private string _outputFolder = string.Empty;

    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(OutputPath))] // Notifica cambiamenti alla proprietà legacy
    private string _outputFileName = string.Empty; 

    // Proprietà calcolata per mostrare l'estensione nella UI (es. ".mp4")
    public string CurrentExtension => _formatProvider.GetExtension(SelectedContainer);

    // [FIX PER WINDOWSERVICE]
    // Questa proprietà ricostruisce il percorso completo per soddisfare le dipendenze esterne
    // che si aspettano ancora di trovare "OutputPath".
    public string OutputPath 
    {
        get 
        {
            if (string.IsNullOrWhiteSpace(OutputFolder)) return string.Empty;
            string finalName = string.IsNullOrWhiteSpace(OutputFileName) ? LocalizationManager.Instance["VideoExportDefaultName"] : OutputFileName;
            return Path.Combine(OutputFolder, finalName + CurrentExtension);
        }
        set 
        {
            // Se WindowService prova a settare il percorso (es. caricando un preset),
            // proviamo a separare Cartella e Nome file.
            if (!string.IsNullOrWhiteSpace(value))
            {
                try 
                {
                    OutputFolder = Path.GetDirectoryName(value) ?? string.Empty;
                    OutputFileName = Path.GetFileNameWithoutExtension(value);
                }
                catch { /* Ignora percorsi non validi */ }
            }
        }
    }

    // --- PROPRIETÀ CALCOLATE ---
    public bool IsInteractionEnabled => !IsExporting;
    public double CurrentBlackPoint => ActiveRenderer?.BlackPoint ?? 0;
    public double CurrentWhitePoint => ActiveRenderer?.WhitePoint ?? 0;
    public string CurrentImageText => $"{Navigator.DisplayIndex} / {Navigator.TotalCount}";

    // --- PARAMETRI VIDEO ---
    [ObservableProperty] private int _originalWidth;
    [ObservableProperty] private int _originalHeight;
    public int FinalWidth => (int)(OriginalWidth * ScaleFactor) & ~1;
    public int FinalHeight => (int)(OriginalHeight * ScaleFactor) & ~1;
    
    public string DurationText => Fps <= 0 
        ? "0s" 
        : TimeSpan.FromSeconds((double)_sourceFiles.Count / Fps).ToString(@"mm\:ss") + "s";

    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(CurrentExtension))] // Aggiorna UI estensione
    [NotifyPropertyChangedFor(nameof(OutputPath))]       // Aggiorna Path completo
    private VideoContainer _selectedContainer;
    
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

        _statusText = LocalizationManager.Instance["StatusReady"];

        // Imposta una cartella di default (Documenti)
        OutputFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        // Imposta il nome file di default basato sul primo file della sequenza
        if (_sourceFiles.Any())
        {
            var rawName = _sourceFiles[0].FileName;
            OutputFileName = Path.GetFileNameWithoutExtension(rawName);
        }
        else
        {
            OutputFileName = LocalizationManager.Instance["VideoExportDefaultName"];
        }

        // Inizializza Contenitori e Codec
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
        
            var imageHdu = data.FirstImageHdu ?? data.PrimaryHdu;
            if (imageHdu == null) return;

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
        
        if (supported.Contains(VideoCodec.H264))
            SelectedCodec = VideoCodec.H264;
        else
            SelectedCodec = AvailableCodecs.FirstOrDefault();

        ConfirmCommand.NotifyCanExecuteChanged();
    }

    // --- COMANDI PRINCIPALI ---

    [RelayCommand]
    private async Task SelectFolder()
    {
        // Selettore cartella
        var path = await _dialogService.ShowOpenFolderDialogAsync(LocalizationManager.Instance["VideoExportSelectFolderTitle"]);
        if (!string.IsNullOrWhiteSpace(path)) OutputFolder = path;
    }

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private async Task Confirm() 
    { 
        IsExporting = true;
        _exportCts = new CancellationTokenSource();

        try 
        {
            // Usiamo la proprietà calcolata OutputPath che unisce tutto
            var settings = GetSettings(OutputPath);
            
            var progress = new Progress<double>(p => {
                ExportProgress = p;
                if (p >= 99) 
                    StatusText = LocalizationManager.Instance["VideoExportStatusFinalizing"];
                else 
                    StatusText = string.Format(LocalizationManager.Instance["VideoExportStatusProcessing"], p);
            });

            await _videoCoordinator.ExportVideoAsync(
                _sourceFiles, 
                settings, 
                settings.InitialProfile, 
                progress, 
                _exportCts.Token);

            StatusText = LocalizationManager.Instance["VideoExportStatusSuccess"];
            ExportProgress = 100;
            await Task.Delay(1000); 

            DialogResult = true; 
            RequestClose?.Invoke(); 
        }
        catch (OperationCanceledException) { StatusText = LocalizationManager.Instance["StatusCancelled"]; }
        catch (Exception) { StatusText = LocalizationManager.Instance["VideoExportStatusError"];  }
        finally 
        { 
            IsExporting = false;
            _exportCts?.Dispose();
            _exportCts = null;
        }
    }

    private bool CanConfirm() => !IsExporting && !string.IsNullOrWhiteSpace(OutputFolder) && SelectedCodec != null;

    [RelayCommand] 
    private void Cancel() 
    { 
        if (IsExporting) 
        {
            _exportCts?.Cancel();
            StatusText = LocalizationManager.Instance["VideoExportStatusCancelling"];
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