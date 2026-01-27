using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KomaLab.Infrastructure;
using KomaLab.Models.Visualization;
using Avalonia; // Per l'uso di Size

namespace KomaLab.ViewModels.ImageProcessing;

/// <summary>
/// ViewModel per la configurazione dei parametri di esportazione video.
/// Gestisce la selezione dinamica, il calcolo della risoluzione finale e della durata.
/// </summary>
public partial class VideoExportToolViewModel : ObservableObject, IDisposable
{
    private readonly IVideoFormatProvider _formatProvider;
    private readonly int _totalFrames;

    // Evento per la chiusura della View gestito dal WindowService
    public event Action? RequestClose;

    // Esito del dialogo (Confermato/Annullato)
    public bool DialogResult { get; private set; }

    // --- Dimensioni e Risoluzione ---
    [ObservableProperty] private int _originalWidth;
    [ObservableProperty] private int _originalHeight;

    // Proprietà calcolate per la UI (forzano dimensioni pari per compatibilità codec)
    public int FinalWidth => (int)(OriginalWidth * ScaleFactor) & ~1;
    public int FinalHeight => (int)(OriginalHeight * ScaleFactor) & ~1;

    // --- Statistiche Video ---
    public string DurationText
    {
        get
        {
            if (Fps <= 0) return "0s";
            double seconds = _totalFrames / Fps;
            return TimeSpan.FromSeconds(seconds).ToString(@"mm\:ss") + "s";
        }
    }

    // --- Opzioni di Formato ---
    [ObservableProperty] private VideoContainer _selectedContainer;
    [ObservableProperty] private VideoCodec _selectedCodec;
    public ObservableCollection<VideoContainer> Containers { get; }
    [ObservableProperty] private ObservableCollection<VideoCodec> _availableCodecs;

    // --- Impostazioni Video ---
    [ObservableProperty] private double _fps = 24.0;
    [ObservableProperty] private double _scaleFactor = 1.0; 
    [ObservableProperty] private VisualizationMode _mode;

    public VideoExportToolViewModel(
        IVideoFormatProvider formatProvider, 
        VisualizationMode currentMode, 
        Size originalSize,
        int totalFrames)
    {
        _formatProvider = formatProvider;
        _mode = currentMode;
        _totalFrames = totalFrames;
        
        _originalWidth = (int)originalSize.Width;
        _originalHeight = (int)originalSize.Height;

        // 1. Carichiamo solo i container che hanno superato i test di inizializzazione
        var supportedContainers = _formatProvider.GetSupportedContainers().ToList();
        Containers = new ObservableCollection<VideoContainer>(supportedContainers);
    
        // 2. Impostiamo un default sensato (MP4 se disponibile)
        SelectedContainer = supportedContainers.Contains(VideoContainer.MP4) 
            ? VideoContainer.MP4 
            : supportedContainers.FirstOrDefault();
        
        _availableCodecs = new ObservableCollection<VideoCodec>();
        UpdateCodecs();
    }

    /// <summary>
    /// Aggiorna i codec disponibili e notifica i cambiamenti alla UI.
    /// </summary>
    private void UpdateCodecs()
    {
        var supportedCodecs = _formatProvider.GetSupportedCodecs(SelectedContainer).ToList();
        AvailableCodecs = new ObservableCollection<VideoCodec>(supportedCodecs);
        SelectedCodec = AvailableCodecs.FirstOrDefault();
    }

    // --- Hook per aggiornamenti UI in tempo reale ---

    partial void OnSelectedContainerChanged(VideoContainer value) => UpdateCodecs();

    partial void OnScaleFactorChanged(double value)
    {
        OnPropertyChanged(nameof(FinalWidth));
        OnPropertyChanged(nameof(FinalHeight));
    }

    partial void OnFpsChanged(double value)
    {
        OnPropertyChanged(nameof(DurationText));
    }

    [RelayCommand]
    private void Confirm()
    {
        if (SelectedCodec == default) return;
        DialogResult = true;
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        DialogResult = false;
        RequestClose?.Invoke();
    }

    /// <summary>
    /// Crea l'oggetto impostazioni finale. 
    /// Forza lo stretching adattivo a true per eliminare il flickering.
    /// </summary>
    public VideoExportSettings GetSettings(string outputPath)
    {
        return new VideoExportSettings(
            OutputPath: outputPath,
            Fps: Fps,
            Container: SelectedContainer,
            Codec: SelectedCodec,
            ScaleFactor: ScaleFactor,
            Mode: Mode,
            AdaptiveStretch: true // Forzato internamente come richiesto
        );
    }

    public void Dispose()
    {
        RequestClose = null;
    }
}