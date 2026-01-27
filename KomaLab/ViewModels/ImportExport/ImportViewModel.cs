using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KomaLab.Models.Fits.Structure;
using KomaLab.Models.Processing;
using KomaLab.Services.Fits;         
using KomaLab.Services.Processing.Coordinators;
using KomaLab.Services.UI;

namespace KomaLab.ViewModels.ImportExport;

public partial class ImportViewModel : ObservableObject
{
    private readonly IDialogService _dialogService;
    private readonly ICalibrationCoordinator _calibrationCoordinator;
    private readonly IFitsDataManager _dataManager;

    private CancellationTokenSource? _processingCts;

    // --- Collezioni File ---
    public ObservableCollection<string> LightFiles { get; } = new();
    public ObservableCollection<string> DarkFiles { get; } = new();
    public ObservableCollection<string> FlatFiles { get; } = new();
    public ObservableCollection<string> BiasFiles { get; } = new();

    // --- Stato UI ---
    [ObservableProperty] 
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private bool _isProcessing;
    
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private string _statusText = "Pronto";
    [ObservableProperty] private bool _isInteractionEnabled = true;

    // [NUOVO] Opzione per creare nodi separati sulla Board
    [ObservableProperty] private bool _importAsSeparateNodes = false;

    // --- Risultati per la Board ---
    public List<string>? CalibratedResultPaths { get; private set; }
    public bool DialogResult { get; private set; }
    public event Action? RequestClose;

    public ImportViewModel(
        IDialogService dialogService, 
        ICalibrationCoordinator calibrationCoordinator,
        IFitsDataManager dataManager)
    {
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _calibrationCoordinator = calibrationCoordinator ?? throw new ArgumentNullException(nameof(calibrationCoordinator));
        _dataManager = dataManager ?? throw new ArgumentNullException(nameof(dataManager));
        
        Debug.WriteLine("[ImportVM] Inizializzato.");
    }
    
    // =======================================================================
    // 1. COMANDI DI SELEZIONE
    // =======================================================================
    
    [RelayCommand]
    private async Task AddLights()
    {
        await AddToCollection(LightFiles, "LIGHTS");
        NotifyLightChanges();
    }
    
    public bool HasLights => LightFiles.Any();

    [RelayCommand]
    private async Task AddDarkAsync()
    {
        await AddToCollection(DarkFiles, "DARKS");
        ClearDarksCommand.NotifyCanExecuteChanged(); 
    }

    [RelayCommand]
    private async Task AddFlatAsync()
    {
        await AddToCollection(FlatFiles, "FLATS");
        ClearFlatsCommand.NotifyCanExecuteChanged(); 
    }

    [RelayCommand]
    private async Task AddBiasAsync()
    {
        await AddToCollection(BiasFiles, "BIAS");
        ClearBiasCommand.NotifyCanExecuteChanged(); 
    }

    // --- LOGICA SMART ADD (MEF Support) ---
    private async Task AddToCollection(ObservableCollection<string> collection, string contextLabel)
    {
        IsInteractionEnabled = false; 
        try 
        {
            var paths = await _dialogService.ShowOpenFitsFileDialogAsync();
            if (paths == null) return;

            foreach (var path in paths)
            {
                if (collection.Contains(path)) continue;

                try
                {
                    // Ispezione MEF
                    var dataPackage = await _dataManager.GetDataAsync(path);
                    var imageHdus = dataPackage.Hdus.Where(h => !h.IsEmpty).ToList();

                    if (imageHdus.Count > 1)
                    {
                        StatusText = $"Estrazione estensioni da {System.IO.Path.GetFileName(path)}...";
                        for (int i = 0; i < imageHdus.Count; i++)
                        {
                            var hdu = imageHdus[i];
                            var header = hdu.Header.Clone(); 
                            
                            var tempRef = await _dataManager.SaveAsTemporaryAsync(
                                hdu.PixelData, 
                                header, 
                                $"{contextLabel}_Ext_{i+1}");

                            collection.Add(tempRef.FilePath);
                        }
                    }
                    else
                    {
                        collection.Add(path);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ImportVM] Errore analisi file {path}: {ex.Message}");
                    collection.Add(path);
                }
            }
            StatusText = "Pronto";
        } 
        finally 
        {
            IsInteractionEnabled = true;
            ConfirmCommand.NotifyCanExecuteChanged();
        }
    }

    // =======================================================================
    // 2. COMANDI DI RIMOZIONE
    // =======================================================================

    [RelayCommand] private void RemoveLight(string path) { if (LightFiles.Remove(path)) NotifyLightChanges(); }
    [RelayCommand] private void RemoveDark(string path) { RemoveFromCollection(DarkFiles, path); ClearDarksCommand.NotifyCanExecuteChanged(); }
    [RelayCommand] private void RemoveFlat(string path) { RemoveFromCollection(FlatFiles, path); ClearFlatsCommand.NotifyCanExecuteChanged(); }
    [RelayCommand] private void RemoveBias(string path) { RemoveFromCollection(BiasFiles, path); ClearBiasCommand.NotifyCanExecuteChanged(); }

    private void RemoveFromCollection(ObservableCollection<string> collection, string path)
    {
        if (collection.Remove(path)) ConfirmCommand.NotifyCanExecuteChanged();
    }
    
    private bool CanClearLights() => LightFiles.Count > 0;
    private bool CanClearDarks() => DarkFiles.Count > 0;
    private bool CanClearFlats() => FlatFiles.Count > 0;
    private bool CanClearBias() => BiasFiles.Count > 0;

    [RelayCommand(CanExecute = nameof(CanClearLights))] private void ClearLights() { LightFiles.Clear(); NotifyLightChanges(); }
    [RelayCommand(CanExecute = nameof(CanClearDarks))] private void ClearDarks() { DarkFiles.Clear(); ClearDarksCommand.NotifyCanExecuteChanged(); }
    [RelayCommand(CanExecute = nameof(CanClearFlats))] private void ClearFlats() { FlatFiles.Clear(); ClearFlatsCommand.NotifyCanExecuteChanged(); }
    [RelayCommand(CanExecute = nameof(CanClearBias))] private void ClearBias() { BiasFiles.Clear(); ClearBiasCommand.NotifyCanExecuteChanged(); }

    [RelayCommand]
    private void ClearAll()
    {
        LightFiles.Clear(); DarkFiles.Clear(); FlatFiles.Clear(); BiasFiles.Clear();
        ConfirmCommand.NotifyCanExecuteChanged();
    }

    // =======================================================================
    // 3. LOGICA DI PROCESSO (CONFIRM / CANCEL)
    // =======================================================================

    private bool CanConfirm() => LightFiles.Any() && !IsProcessing;

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private async Task Confirm()
    {
        IsProcessing = true;
        StatusText = "Avvio calibrazione...";
        ProgressValue = 0;
        
        _processingCts?.Cancel();
        _processingCts = new CancellationTokenSource();

        try
        {
            var progress = new Progress<BatchProgressReport>(report =>
            {
                ProgressValue = report.Percentage;
                StatusText = $"Processamento: {report.CurrentFileIndex}/{report.TotalFiles} ({report.CurrentFileName})";
            });

            CalibratedResultPaths = await _calibrationCoordinator.ExecuteCalibrationAsync(
                LightFiles.ToList(), DarkFiles.ToList(), FlatFiles.ToList(), BiasFiles.ToList(),
                progress, _processingCts.Token);

            if (CalibratedResultPaths != null && CalibratedResultPaths.Any())
            {
                DialogResult = true;
                RequestClose?.Invoke();
            }
            else
            {
                StatusText = "Nessun file generato.";
                IsProcessing = false;
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "Operazione annullata.";
            IsProcessing = false;
        }
        catch (Exception ex)
        {
            StatusText = $"Errore: {ex.Message}";
            IsProcessing = false;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        if (IsProcessing) _processingCts?.Cancel();
        else
        {
            DialogResult = false;
            RequestClose?.Invoke();
        }
    }
    
    private void NotifyLightChanges()
    {
        OnPropertyChanged(nameof(HasLights));          
        ConfirmCommand.NotifyCanExecuteChanged();      
        ClearLightsCommand.NotifyCanExecuteChanged();  
    }
}