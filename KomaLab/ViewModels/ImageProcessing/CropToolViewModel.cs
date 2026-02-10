using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KomaLab.Models.Fits;
using KomaLab.Models.Primitives;
using KomaLab.Models.Visualization;
using KomaLab.Services.Factories;
using KomaLab.Services.Fits;
using KomaLab.Services.Processing.Coordinators;
using KomaLab.ViewModels.Shared;
using KomaLab.ViewModels.Visualization;

namespace KomaLab.ViewModels.ImageProcessing;

public partial class CropToolViewModel : ObservableObject, IDisposable
{
    private readonly ICropCoordinator _coordinator;
    private readonly IFitsDataManager _dataManager;
    private readonly IFitsRendererFactory _rendererFactory;
    private readonly List<FitsFileReference> _files;

    // Dati dei centri
    private readonly Point2D?[] _centers; 
    private Point2D? _staticCenter;       

    private CancellationTokenSource? _loadCts;

    // --- Proprietà di Navigazione e Vista ---
    public SequenceNavigator Navigator { get; } = new();
    public CropImageViewport Viewport { get; } = new();

    // --- Risultati per WindowService ---
    public List<string>? ResultPaths { get; private set; }
    public Action? RequestClose { get; set; }

    // --- Stato UI ---
    
    // CORREZIONE QUI: Rimosso l'attributo errato [NotifyPropertyChangedFor]
    [ObservableProperty] 
    private FitsRenderer? _activeRenderer;

    [ObservableProperty] 
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private bool _isBusy;

    [ObservableProperty] private string _statusText = "Inizializzazione...";
    
    // Modalità visualizzazione (passata dall'esterno)
    [ObservableProperty] 
    private VisualizationMode _visualizationMode = VisualizationMode.Linear;

    // --- Parametri Crop ---
    [ObservableProperty] private double _maxAllowedWidth;
    [ObservableProperty] private double _maxAllowedHeight;

    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(IsDynamicMode))]
    [NotifyPropertyChangedFor(nameof(InstructionsText))]
    private CropMode _selectedMode = CropMode.Static;

    [ObservableProperty] private int _cropWidth = 500;
    [ObservableProperty] private int _cropHeight = 500;

    public bool IsDynamicMode => SelectedMode == CropMode.Dynamic;
    
    public string CurrentImageText => $"{Navigator.CurrentIndex + 1} / {Navigator.TotalCount}";
    
    public string InstructionsText => SelectedMode == CropMode.Static 
        ? "Clicca sull'immagine per definire il centro (uguale per tutte)." 
        : "Clicca su OGNI immagine per definire il centro specifico.";

    public CropToolViewModel(
        List<FitsFileReference> files,
        ICropCoordinator coordinator,
        IFitsDataManager dataManager,
        IFitsRendererFactory rendererFactory)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _dataManager = dataManager ?? throw new ArgumentNullException(nameof(dataManager));
        _rendererFactory = rendererFactory ?? throw new ArgumentNullException(nameof(rendererFactory));
        
        _centers = new Point2D?[_files.Count];

        Navigator.UpdateStatus(0, _files.Count);
        Navigator.IndexChanged += OnNavigatorIndexChanged;

        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        IsBusy = true;
        try
        {
            var minSize = await _coordinator.AnalyzeSequenceLimitsAsync(_files);
            MaxAllowedWidth = minSize.Width;
            MaxAllowedHeight = minSize.Height;

            // Default sicuri (50% della dimensione minima)
            CropWidth = Math.Min(500, (int)(MaxAllowedWidth / 2));
            CropHeight = Math.Min(500, (int)(MaxAllowedHeight / 2));

            await LoadImageAsync(0);
            StatusText = "Pronto. Imposta il centro.";
        }
        catch (Exception ex)
        {
            StatusText = $"Errore inizializzazione: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    // --- Gestione Immagini ---

    private async void OnNavigatorIndexChanged(object? sender, int index)
    {
        OnPropertyChanged(nameof(CurrentImageText)); 
        await LoadImageAsync(index);
    }

    private async Task LoadImageAsync(int index)
    {
        if (index < 0 || index >= _files.Count) return;

        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var token = _loadCts.Token;

        // NOTA: Abbiamo rimosso IsBusy = true qui per evitare 
        // che la barra di progresso compaia ad ogni cambio immagine.
        
        try
        {
            var fileRef = _files[index];
            var data = await _dataManager.GetDataAsync(fileRef.FilePath);
            var imgHdu = data.FirstImageHdu ?? data.PrimaryHdu;
            
            if (imgHdu == null) throw new Exception("Nessuna immagine valida.");

            var newRenderer = await _rendererFactory.CreateAsync(imgHdu.PixelData, fileRef.ModifiedHeader ?? imgHdu.Header);
            newRenderer.VisualizationMode = VisualizationMode;

            // Mantieni lo stretch (contrasto) quando cambi immagine
            if (ActiveRenderer != null)
            {
                newRenderer.ApplyRelativeProfile(ActiveRenderer.CaptureSigmaProfile());
                ActiveRenderer.Dispose();
            }
            else
            {
                await newRenderer.ResetThresholdsAsync();
            }

            if (token.IsCancellationRequested) 
            {
                newRenderer.Dispose();
                return;
            }

            ActiveRenderer = newRenderer;
            Viewport.ImageSize = ActiveRenderer.ImageSize; 
            
            UpdateViewportOverlay(index);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusText = $"Errore caricamento: {ex.Message}"; }
        // Rimosso il finally { IsBusy = false }
    }

    // --- Logica Interazione (Centri) ---

    public void SetCenter(Point imagePoint)
    {
        if (IsBusy) return;

        int index = Navigator.CurrentIndex;
        var center = new Point2D(imagePoint.X, imagePoint.Y);

        if (SelectedMode == CropMode.Static)
        {
            _staticCenter = center;
            StatusText = $"Centro statico impostato: {center.X:F0}, {center.Y:F0}";
        }
        else
        {
            _centers[index] = center;
            StatusText = $"Centro impostato per immagine {index + 1}";
        }

        UpdateViewportOverlay(index);
        ApplyCommand.NotifyCanExecuteChanged(); // Aggiorna stato pulsante Applica
    }

    public void ClearCenter()
    {
        if (IsBusy) return;

        int index = Navigator.CurrentIndex;

        if (SelectedMode == CropMode.Static)
        {
            _staticCenter = null;
            StatusText = "Centro statico rimosso.";
        }
        else
        {
            _centers[index] = null;
            StatusText = $"Centro rimosso per immagine {index + 1}.";
        }

        UpdateViewportOverlay(index);
        ApplyCommand.NotifyCanExecuteChanged(); // Aggiorna stato pulsante Applica
    }

    private void UpdateViewportOverlay(int index)
    {
        Point2D? centerToShow = (SelectedMode == CropMode.Static) ? _staticCenter : _centers[index];

        if (centerToShow.HasValue)
        {
            Viewport.SetCropGeometry(
                new Point(centerToShow.Value.X, centerToShow.Value.Y), 
                CropWidth, 
                CropHeight);
        }
        else
        {
            Viewport.ClearCrop();
        }
    }

    // --- Property Changes ---

    partial void OnCropWidthChanged(int value) => UpdateViewportOverlay(Navigator.CurrentIndex);
    partial void OnCropHeightChanged(int value) => UpdateViewportOverlay(Navigator.CurrentIndex);
    
    partial void OnSelectedModeChanged(CropMode value) 
    {
        UpdateViewportOverlay(Navigator.CurrentIndex);
        ApplyCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(InstructionsText));
    }
    
    partial void OnVisualizationModeChanged(VisualizationMode value)
    {
        if (ActiveRenderer != null) ActiveRenderer.VisualizationMode = value;
    }

    // --- Azione Apply ---

    private bool CanApply()
    {
        if (IsBusy) return false;

        if (SelectedMode == CropMode.Static)
        {
            return _staticCenter != null;
        }
        else
        {
            // In dinamica tutti i frame devono avere un centro
            return _centers.All(c => c != null);
        }
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task Apply()
    {
        if (IsBusy) return;
        
        // Doppia verifica (ridondante ma sicura)
        if (!CanApply())
        {
            // Se siamo qui in modalità dinamica, aiutiamo l'utente a trovare il frame mancante
            if (SelectedMode == CropMode.Dynamic)
            {
                int missing = Array.IndexOf(_centers, null);
                if (missing >= 0)
                {
                    StatusText = $"Manca il centro per l'immagine {missing + 1}.";
                    Navigator.MoveTo(missing);
                }
            }
            return;
        }

        IsBusy = true;
        StatusText = "Elaborazione in corso...";

        try
        {
            var size = new Size2D(CropWidth, CropHeight);
            
            // Prepara la lista centri normalizzata
            var centersList = (SelectedMode == CropMode.Static)
                ? Enumerable.Repeat(_staticCenter, _files.Count).ToList()
                : _centers.ToList();

            ResultPaths = await _coordinator.ExecuteCropBatchAsync(
                _files,
                centersList,
                size);

            StatusText = "Fatto!";
            RequestClose?.Invoke();
        }
        catch (Exception ex)
        {
            StatusText = $"Errore: {ex.Message}";
        }
        finally 
        { 
            IsBusy = false; 
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        ResultPaths = null;
        RequestClose?.Invoke();
    }

    // --- View Utils ---

    [RelayCommand] public void ResetView() => Viewport.ResetView();
    
    [RelayCommand] 
    public async Task ResetThresholds() 
    { 
        if(ActiveRenderer != null) await ActiveRenderer.ResetThresholdsAsync(); 
    }

    public void Dispose()
    {
        _loadCts?.Cancel();
        Navigator.IndexChanged -= OnNavigatorIndexChanged;
        ActiveRenderer?.Dispose();
        GC.SuppressFinalize(this);
    }
}