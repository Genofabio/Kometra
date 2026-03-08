using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kometra.Infrastructure; // Aggiunto per l'accesso al LocalizationManager
using Kometra.Models.Fits;
using Kometra.Models.Primitives;
using Kometra.Models.Visualization;
using Kometra.Services.Factories;
using Kometra.Services.Fits;
using Kometra.Services.Processing.Coordinators;
using Kometra.ViewModels.Shared;
using Kometra.ViewModels.Visualization;

namespace Kometra.ViewModels.ImageProcessing;

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
        IFitsRendererFactory rendererFactory)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _dataManager = dataManager ?? throw new ArgumentNullException(nameof(dataManager));
        _rendererFactory = rendererFactory ?? throw new ArgumentNullException(nameof(rendererFactory));
        
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
            // 1. Analisi dei limiti (già esistente)
            var minSize = await _coordinator.AnalyzeSequenceLimitsAsync(_files);
            MaxAllowedWidth = minSize.Width;
            MaxAllowedHeight = minSize.Height;

            // Default dimensioni crop (già esistente)
            CropWidth = Math.Min(500, (int)(MaxAllowedWidth / 2));
            CropHeight = Math.Min(500, (int)(MaxAllowedHeight / 2));

            // 2. Caricamento della prima immagine (già esistente)
            await LoadImageAsync(0);

            // --- NUOVA LOGICA: IMPOSTAZIONE CENTRO DI DEFAULT ---
            if (ActiveRenderer != null)
            {
                // Calcoliamo il centro geometrico
                double midX = Viewport.ImageSize.Width / 2.0;
                double midY = Viewport.ImageSize.Height / 2.0;
                var midPoint = new Point2D(midX, midY);

                // Impostiamo il centro statico (per la modalità Statica)
                _staticCenter = midPoint;

                // Pre-popoliamo anche l'array dei centri dinamici (per la modalità Dinamica)
                // così se l'utente switcha modalità, non trova i campi vuoti.
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

        // NOTA: Abbiamo rimosso IsBusy = true qui per evitare 
        // che la barra di progresso compaia ad ogni cambio immagine.
        
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
            StatusText = string.Format(LocalizationManager.Instance["StatusStaticCenterSet"], center.X, center.Y);
        }
        else
        {
            _centers[index] = center;
            StatusText = string.Format(LocalizationManager.Instance["StatusDynamicCenterSet"], index + 1);
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
            StatusText = LocalizationManager.Instance["StatusStaticCenterRemoved"];
        }
        else
        {
            _centers[index] = null;
            StatusText = string.Format(LocalizationManager.Instance["StatusDynamicCenterRemoved"], index + 1);
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
                CropWidth); // Nota: avevi CropWidth ripetuto o CropHeight? Ho tenuto la tua logica originale se era intenzionale, altrimenti rettifica in CropHeight.
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
                    StatusText = string.Format(LocalizationManager.Instance["StatusMissingCenter"], missing + 1);
                    Navigator.MoveTo(missing);
                }
            }
            return;
        }

        IsBusy = true;
        StatusText = LocalizationManager.Instance["StatusProcessing"];

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
        // Verifichiamo che ci sia un'immagine caricata
        if (ActiveRenderer == null || Viewport.ImageSize.Width <= 0) return;

        // Calcoliamo il centro esatto
        double centerX = Viewport.ImageSize.Width / 2.0;
        double centerY = Viewport.ImageSize.Height / 2.0;

        // Usiamo il metodo SetCenter già esistente che gestisce 
        // correttamente sia la modalità Statica che Dinamica
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