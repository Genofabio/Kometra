using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KomaLab.Infrastructure;
using KomaLab.Models.Fits;
using KomaLab.Models.Visualization;
using KomaLab.Services.Factories;
using KomaLab.Services.Fits;
using KomaLab.ViewModels.Shared;
using KomaLab.ViewModels.Visualization;

namespace KomaLab.ViewModels.ImportExport;

public partial class VideoExportToolViewModel : ObservableObject, IDisposable
{
    private readonly IVideoFormatProvider _formatProvider;
    private readonly IFitsDataManager _dataManager;
    private readonly IFitsRendererFactory _rendererFactory;
    private readonly IReadOnlyList<FitsFileReference> _sourceFiles;

    public event Action? RequestClose;
    public bool DialogResult { get; private set; }

    // Task per segnalare alla View che l'immagine è pronta (usato per il centramento automatico)
    public TaskCompletionSource<bool> ImageLoadedTcs { get; private set; } = new();

    // --- Viewport e Navigazione ---
    public SequenceNavigator Navigator { get; } = new();
    public EnhancementImageViewport Viewport { get; } = new();
    
    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(CurrentImageText))]
    [NotifyPropertyChangedFor(nameof(CurrentBlackPoint))]
    [NotifyPropertyChangedFor(nameof(CurrentWhitePoint))]
    private FitsRenderer? _activeRenderer;
    
    public double CurrentBlackPoint => ActiveRenderer?.BlackPoint ?? 0;
    public double CurrentWhitePoint => ActiveRenderer?.WhitePoint ?? 0;
    public string CurrentImageText => $"{Navigator.DisplayIndex} / {Navigator.TotalCount}";

    // --- Dimensioni e Risoluzione ---
    [ObservableProperty] private int _originalWidth;
    [ObservableProperty] private int _originalHeight;
    
    // Calcolo risoluzione con bitwise AND per garantire numeri PARI (fondamentale per H264)
    public int FinalWidth => (int)(OriginalWidth * ScaleFactor) & ~1;
    public int FinalHeight => (int)(OriginalHeight * ScaleFactor) & ~1;

    // --- Statistiche Video ---
    public string DurationText => Fps <= 0 ? "0s" : TimeSpan.FromSeconds((double)_sourceFiles.Count / Fps).ToString(@"mm\:ss") + "s";

    // --- Proprietà UI ---
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
        IReadOnlyList<FitsFileReference> files,
        VisualizationMode currentMode, 
        Size originalSize)
    {
        _formatProvider = formatProvider;
        _dataManager = dataManager;
        _rendererFactory = rendererFactory;
        _sourceFiles = files;
        _mode = currentMode;
        
        _originalWidth = (int)originalSize.Width;
        _originalHeight = (int)originalSize.Height;

        // Inizializzazione Liste
        var supported = _formatProvider.GetSupportedContainers().ToList();
        Containers = new ObservableCollection<VideoContainer>(supported);
        SelectedContainer = supported.Contains(VideoContainer.MP4) ? VideoContainer.MP4 : supported.FirstOrDefault();
        
        _availableCodecs = new ObservableCollection<VideoCodec>();
        UpdateCodecs();

        // Configurazione Navigazione
        Navigator.UpdateStatus(0, _sourceFiles.Count);
        Navigator.IndexChanged += OnNavigatorIndexChanged;

        // Notifica iniziale per proprietà calcolate
        OnPropertyChanged(nameof(FinalWidth));
        OnPropertyChanged(nameof(FinalHeight));
        OnPropertyChanged(nameof(DurationText));
        
        // Caricamento asincrono del primo frame
        _ = LoadImageAsync(0);
    }

    private async void OnNavigatorIndexChanged(object? sender, int index)
    {
        await LoadImageAsync(index);
    }

    private async Task LoadImageAsync(int index)
    {
        try
        {
            if (index < 0 || index >= _sourceFiles.Count) return;

            var fileRef = _sourceFiles[index];
            var data = await _dataManager.GetDataAsync(fileRef.FilePath);
            var newRenderer = await _rendererFactory.CreateAsync(data.PixelData, fileRef.ModifiedHeader ?? data.Header);
            
            newRenderer.VisualizationMode = Mode;

            // Se l'utente ha già regolato lo stretching, lo manteniamo tra i frame
            if (ActiveRenderer != null)
            {
                var profile = ActiveRenderer.CaptureSigmaProfile();
                newRenderer.ApplyRelativeProfile(profile);
            }
            else 
            {
                await newRenderer.ResetThresholdsAsync();
            }

            ActiveRenderer?.Dispose();
            ActiveRenderer = newRenderer;
            Viewport.ImageSize = ActiveRenderer.ImageSize;
            
            NotifyThresholdsChanged();
            
            if (!ImageLoadedTcs.Task.IsCompleted)
            {
                ImageLoadedTcs.TrySetResult(true);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Errore caricamento anteprima export: {ex.Message}");
            if (!ImageLoadedTcs.Task.IsCompleted) ImageLoadedTcs.TrySetException(ex);
        }
    }

    private void UpdateCodecs()
    {
        var supported = _formatProvider.GetSupportedCodecs(SelectedContainer).ToList();
        AvailableCodecs = new ObservableCollection<VideoCodec>(supported);
    
        // Seleziona il primo codec utile
        SelectedCodec = AvailableCodecs.FirstOrDefault();
    
        // FIX: Notifica al comando che lo stato è cambiato (abilita il pulsante Esporta)
        ConfirmCommand.NotifyCanExecuteChanged();
    }

    // Aggiorna il comando Confirm con la validazione
    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Confirm() 
    { 
        DialogResult = true; 
        RequestClose?.Invoke(); 
    }

    private bool CanConfirm() => SelectedCodec != null;

    public void NotifyThresholdsChanged()
    {
        OnPropertyChanged(nameof(CurrentBlackPoint));
        OnPropertyChanged(nameof(CurrentWhitePoint));
    }

    // --- Hooks di aggiornamento UI ---

    partial void OnSelectedContainerChanged(VideoContainer value) => UpdateCodecs();

    partial void OnScaleFactorChanged(double value) 
    { 
        OnPropertyChanged(nameof(FinalWidth)); 
        OnPropertyChanged(nameof(FinalHeight)); 
    }

    partial void OnFpsChanged(double value) => OnPropertyChanged(nameof(DurationText));

    // --- Comandi ---

    [RelayCommand] 
    private void Cancel() 
    { 
        DialogResult = false; 
        RequestClose?.Invoke(); 
    }

    [RelayCommand] 
    private async Task ResetThresholds() 
    { 
        if (ActiveRenderer != null)
        {
            await ActiveRenderer.ResetThresholdsAsync();
            NotifyThresholdsChanged();
        }
    }

    public VideoExportSettings GetSettings(string outputPath) 
    {
        // CATTURIAMO LE SOGLIE CORRENTI DALLA VIEWPORT DEL TOOL
        // Se l'ActiveRenderer è nullo (impossibile a questo punto), usiamo un default
        var currentProfile = ActiveRenderer?.CaptureContrastProfile() 
                             ?? new AbsoluteContrastProfile(0, 65535);

        return new VideoExportSettings(
            OutputPath: outputPath, 
            Fps: Fps, 
            Container: SelectedContainer, 
            Codec: SelectedCodec, 
            ScaleFactor: ScaleFactor, 
            Mode: Mode, 
            AdaptiveStretch: true,
            InitialProfile: currentProfile // <--- PASSAGGIO CHIAVE
        );
    }
    
    public void Dispose()
    {
        RequestClose = null;
        ActiveRenderer?.Dispose();
        Navigator.IndexChanged -= OnNavigatorIndexChanged;
    }
}