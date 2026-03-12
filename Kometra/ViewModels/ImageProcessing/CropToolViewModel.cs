using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kometra.Infrastructure; 
using Kometra.Models.Fits;
using Kometra.Models.Primitives;
using Kometra.Models.Visualization;
using Kometra.Services.Factories;
using Kometra.Services.Fits;
using Kometra.Services.Processing.Coordinators;
using Kometra.Services.Settings;
using Kometra.ViewModels.Shared;
using Kometra.ViewModels.Visualization;

namespace Kometra.ViewModels.ImageProcessing;

public partial class CropToolViewModel : ObservableObject, IDisposable
{
    private readonly ICropCoordinator _coordinator;
    private readonly IFitsDataManager _dataManager;
    private readonly IFitsRendererFactory _rendererFactory;
    private readonly IToolParametersCache _parametersCache; 
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
    
    [ObservableProperty] 
    private FitsRenderer? _activeRenderer;

    [ObservableProperty] 
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private bool _isBusy;

    [ObservableProperty] private string _statusText = string.Empty;
    
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
        ? LocalizationManager.Instance["CropStaticInstructions"] 
        : LocalizationManager.Instance["CropDynamicInstructions"];

    public CropToolViewModel(
        List<FitsFileReference> files,
        ICropCoordinator coordinator,
        IFitsDataManager dataManager,
        IFitsRendererFactory rendererFactory,
        IToolParametersCache parametersCache) 
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _dataManager = dataManager ?? throw new ArgumentNullException(nameof(dataManager));
        _rendererFactory = rendererFactory ?? throw new ArgumentNullException(nameof(rendererFactory));
        _parametersCache = parametersCache ?? throw new ArgumentNullException(nameof(parametersCache));
        
        _centers = new Point2D?[_files.Count];

        // Inizializzazione testo di stato
        StatusText = LocalizationManager.Instance["StatusInit"];

        Navigator.UpdateStatus(0, _files.Count);
        Navigator.IndexChanged += OnNavigatorIndexChanged;

        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        IsBusy = true;
        try
        {
            // 1. Analisi dei limiti
            var minSize = await _coordinator.AnalyzeSequenceLimitsAsync(_files);
            MaxAllowedWidth = minSize.Width;
            MaxAllowedHeight = minSize.Height;

            // --- LETTURA DEI PARAMETRI DAL MODEL DI CONFIGURAZIONE ---
            var settings = _parametersCache.Crop;
            
            SelectedMode = settings.Mode; 
            
            CropWidth = (int)Math.Min(MaxAllowedWidth, settings.Width);
            CropHeight = (int)Math.Min(MaxAllowedHeight, settings.Height);

            // 2. Caricamento della prima immagine
            await LoadImageAsync(0);

            // --- IMPOSTAZIONE CENTRO DI DEFAULT ---
            if (ActiveRenderer != null)
            {
                // Calcoliamo il centro geometrico
                double midX = Viewport.ImageSize.Width / 2.0;
                double midY = Viewport.ImageSize.Height / 2.0;
                var midPoint = new Point2D(midX, midY);

                // Impostiamo il centro statico (per la modalità Statica)
                _staticCenter = midPoint;

                // Pre-popoliamo anche l'array dei centri dinamici (per la modalità Dinamica)
                for (int i = 0; i < _centers.Length; i++)
                {
                    _centers[i] = midPoint;
                }

                // Forza l'aggiornamento visivo dell'overlay (mirino)
                UpdateViewportOverlay(Navigator.CurrentIndex);
            
                // Notifica al comando Apply che ora può essere eseguito
                ApplyCommand.NotifyCanExecuteChanged();
            }

            StatusText = LocalizationManager.Instance["StatusReadyCenterInit"];
        }
        catch (Exception ex)
        {
            StatusText = string.Format(LocalizationManager.Instance["ErrorInit"], ex.Message);
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

        try
        {
            var fileRef = _files[index];
            var data = await _dataManager.GetDataAsync(fileRef.FilePath);
            var imgHdu = data.FirstImageHdu ?? data.PrimaryHdu;
            
            if (imgHdu == null) throw new Exception(LocalizationManager.Instance["ErrorNoValidImage"]);

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
        catch (Exception ex) { StatusText = string.Format(LocalizationManager.Instance["ErrorLoading"], ex.Message); }
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
            StatusText = string.Format(LocalizationManager.Instance["StatusStaticCenterSet"], center.X, center.Y);
        }
        else
        {
            _centers[index] = center;
            StatusText = string.Format(LocalizationManager.Instance["StatusDynamicCenterSet"], index + 1);
        }

        UpdateViewportOverlay(index);
        ApplyCommand.NotifyCanExecuteChanged(); 
    }

    public void ClearCenter()
    {
        if (IsBusy) return;

        int index = Navigator.CurrentIndex;

        if (SelectedMode == CropMode.Static)
        {
            _staticCenter = null;
            StatusText = LocalizationManager.Instance["StatusStaticCenterRemoved"];
        }
        else
        {
            _centers[index] = null;
            StatusText = string.Format(LocalizationManager.Instance["StatusDynamicCenterRemoved"], index + 1);
        }

        UpdateViewportOverlay(index);
        ApplyCommand.NotifyCanExecuteChanged();
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
            return _centers.All(c => c != null);
        }
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task Apply()
    {
        if (IsBusy) return;
        
        if (!CanApply())
        {
            if (SelectedMode == CropMode.Dynamic)
            {
                int missing = Array.IndexOf(_centers, null);
                if (missing >= 0)
                {
                    StatusText = string.Format(LocalizationManager.Instance["StatusMissingCenter"], missing + 1);
                    Navigator.MoveTo(missing);
                }
            }
            return;
        }

        IsBusy = true;
        StatusText = LocalizationManager.Instance["StatusProcessing"];

        // --- SALVATAGGIO DEI PARAMETRI NEL MODEL ---
        _parametersCache.Crop.Mode = SelectedMode;
        _parametersCache.Crop.Width = CropWidth;
        _parametersCache.Crop.Height = CropHeight;

        try
        {
            var size = new Size2D(CropWidth, CropHeight);
            
            var centersList = (SelectedMode == CropMode.Static)
                ? Enumerable.Repeat(_staticCenter, _files.Count).ToList()
                : _centers.ToList();

            ResultPaths = await _coordinator.ExecuteCropBatchAsync(
                _files,
                centersList,
                size);

            StatusText = LocalizationManager.Instance["StatusDone"];
            RequestClose?.Invoke();
        }
        catch (Exception ex)
        {
            StatusText = string.Format(LocalizationManager.Instance["ErrorGeneric"], ex.Message);
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
    public void SetCenterToImageMiddle()
    {
        if (ActiveRenderer == null || Viewport.ImageSize.Width <= 0) return;

        double centerX = Viewport.ImageSize.Width / 2.0;
        double centerY = Viewport.ImageSize.Height / 2.0;

        SetCenter(new Point(centerX, centerY));
    
        StatusText = LocalizationManager.Instance["StatusCenterGeometricInit"];
    }
    
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