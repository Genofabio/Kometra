using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KomaLab.Models.Processing;
using KomaLab.Services.Processing.Coordinators;
using KomaLab.Services.UI;

namespace KomaLab.ViewModels.ImportExport;

/// <summary>
/// ViewModel per la gestione dell'importazione di file FITS con calibrazione opzionale.
/// </summary>
public partial class ImportViewModel : ObservableObject
{
    private readonly IDialogService _dialogService;
    private readonly ICalibrationCoordinator _calibrationCoordinator;
    private CancellationTokenSource? _processingCts;

    // --- Collezioni File ---
    public ObservableCollection<string> LightFiles { get; } = new();
    public ObservableCollection<string> DarkFiles { get; } = new();
    public ObservableCollection<string> FlatFiles { get; } = new();
    public ObservableCollection<string> BiasFiles { get; } = new();

    // --- Stato UI ---
    [ObservableProperty] 
    [NotifyCanExecuteChangedFor(nameof(ImportViewModel.ConfirmCommand))]
    private bool _isProcessing;
    
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private string _statusText = "Pronto";

    // --- Risultati per la Board ---
    public List<string>? CalibratedResultPaths { get; private set; }
    public bool DialogResult { get; private set; }
    public event Action? RequestClose;

    public ImportViewModel(IDialogService dialogService, ICalibrationCoordinator calibrationCoordinator)
    {
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _calibrationCoordinator = calibrationCoordinator ?? throw new ArgumentNullException(nameof(calibrationCoordinator));
        
        Debug.WriteLine("[ImportVM] Inizializzato.");
    }
    
    

    // =======================================================================
    // 1. COMANDI DI SELEZIONE
    // =======================================================================
    
    [RelayCommand]
    private async Task AddLights()
    {
        IsInteractionEnabled = false;
        try {
            var paths = await _dialogService.ShowOpenFitsFileDialogAsync();
            if (paths != null) {
                foreach (var p in paths) LightFiles.Add(p);
                NotifyLightChanges(); // Aggiorna stato Confirm e ClearLights
            }
        } finally {
            IsInteractionEnabled = true;
        }
    }
    
    public bool HasLights => LightFiles.Any();

    [RelayCommand]
    private async Task AddDarkAsync()
    {
        await AddToCollection(DarkFiles, "DARKS");
        ClearDarksCommand.NotifyCanExecuteChanged(); // <--- AGGIUNTO
    }

    [RelayCommand]
    private async Task AddFlatAsync()
    {
        await AddToCollection(FlatFiles, "FLATS");
        ClearFlatsCommand.NotifyCanExecuteChanged(); // <--- AGGIUNTO
    }

    [RelayCommand]
    private async Task AddBiasAsync()
    {
        await AddToCollection(BiasFiles, "BIAS");
        ClearBiasCommand.NotifyCanExecuteChanged(); // <--- AGGIUNTO
    }

    // Nel tuo ImportViewModel.cs
    [ObservableProperty] private bool _isInteractionEnabled = true;

    private async Task AddToCollection(ObservableCollection<string> collection, string label)
    {
        IsInteractionEnabled = false; 
        try {
            var paths = await _dialogService.ShowOpenFitsFileDialogAsync();
            if (paths == null) return;
            foreach (var path in paths) if (!collection.Contains(path)) collection.Add(path);
        } finally {
            IsInteractionEnabled = true;
            ConfirmCommand.NotifyCanExecuteChanged();
        }
    }

    // =======================================================================
    // 2. COMANDI DI RIMOZIONE
    // =======================================================================

    [RelayCommand] 
    private void RemoveLight(string path) 
    {
        if (LightFiles.Remove(path)) NotifyLightChanges();
    }

    [RelayCommand] 
    private void RemoveDark(string path) 
    {
        RemoveFromCollection(DarkFiles, path);
        ClearDarksCommand.NotifyCanExecuteChanged(); // <--- AGGIUNTO
    }

    [RelayCommand] 
    private void RemoveFlat(string path) 
    {
        RemoveFromCollection(FlatFiles, path);
        ClearFlatsCommand.NotifyCanExecuteChanged(); // <--- AGGIUNTO
    }

    [RelayCommand] 
    private void RemoveBias(string path) 
    {
        RemoveFromCollection(BiasFiles, path);
        ClearBiasCommand.NotifyCanExecuteChanged(); // <--- AGGIUNTO
    }

    private void RemoveFromCollection(ObservableCollection<string> collection, string path)
    {
        if (collection.Remove(path))
        {
            ConfirmCommand.NotifyCanExecuteChanged();
        }
    }
    
    private bool CanClearLights() => LightFiles.Count > 0;
    private bool CanClearDarks() => DarkFiles.Count > 0;
    private bool CanClearFlats() => FlatFiles.Count > 0;
    private bool CanClearBias() => BiasFiles.Count > 0;

// Collega la condizione al comando tramite CanExecute
    [RelayCommand(CanExecute = nameof(CanClearLights))]
    private void ClearLights()
    {
        LightFiles.Clear();
        NotifyLightChanges(); // Metodo helper per aggiornare UI e comandi
    }

    [RelayCommand(CanExecute = nameof(CanClearDarks))]
    private void ClearDarks()
    {
        DarkFiles.Clear();
        ClearDarksCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanClearFlats))]
    private void ClearFlats()
    {
        FlatFiles.Clear();
        ClearFlatsCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanClearBias))]
    private void ClearBias()
    {
        BiasFiles.Clear();
        ClearBiasCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void ClearAll()
    {
        LightFiles.Clear();
        DarkFiles.Clear();
        FlatFiles.Clear();
        BiasFiles.Clear();
        ConfirmCommand.NotifyCanExecuteChanged();
    }

    // =======================================================================
    // 3. LOGICA DI PROCESSO (CONFIRM / CANCEL)
    // =======================================================================

    private bool CanConfirm() => LightFiles.Any() && !IsProcessing;

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private async Task Confirm()
    {
        Debug.WriteLine("[ImportVM] Esecuzione Confirm avviata.");
        IsProcessing = true;
        StatusText = "Avvio calibrazione...";
        ProgressValue = 0;
        
        _processingCts?.Cancel();
        _processingCts = new CancellationTokenSource();

        try
        {
            // Debug delle liste prima dell'invio
            Debug.WriteLine($"[ImportVM] Invio al Coordinator: L:{LightFiles.Count} D:{DarkFiles.Count} F:{FlatFiles.Count} B:{BiasFiles.Count}");

            var progress = new Progress<BatchProgressReport>(report =>
            {
                ProgressValue = report.Percentage;
                StatusText = $"Processamento: {report.CurrentFileIndex}/{report.TotalFiles} ({report.CurrentFileName})";
            });

            // ESECUZIONE CALIBRAZIONE
            CalibratedResultPaths = await _calibrationCoordinator.ExecuteCalibrationAsync(
                LightFiles.ToList(),
                DarkFiles.ToList(),
                FlatFiles.ToList(),
                BiasFiles.ToList(),
                progress,
                _processingCts.Token);

            if (CalibratedResultPaths != null && CalibratedResultPaths.Any())
            {
                Debug.WriteLine($"[ImportVM] Successo! Ricevuti {CalibratedResultPaths.Count} percorsi calibrati.");
                DialogResult = true;
                RequestClose?.Invoke();
            }
            else
            {
                Debug.WriteLine("[ImportVM] ATTENZIONE: Il coordinator ha restituito una lista vuota o nulla.");
                StatusText = "Nessun file generato.";
                IsProcessing = false;
            }
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine("[ImportVM] Operazione annullata dall'utente.");
            StatusText = "Operazione annullata.";
            IsProcessing = false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ImportVM] ERRORE CRITICO: {ex.Message}");
            Debug.WriteLine($"[ImportVM] StackTrace: {ex.StackTrace}");
            StatusText = $"Errore: {ex.Message}";
            IsProcessing = false;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        if (IsProcessing)
        {
            Debug.WriteLine("[ImportVM] Richiesta cancellazione task in corso...");
            _processingCts?.Cancel();
        }
        else
        {
            Debug.WriteLine("[ImportVM] Chiusura finestra (Annulla).");
            DialogResult = false;
            RequestClose?.Invoke();
        }
    }
    
    // Helper per centralizzare gli aggiornamenti relativi ai Lights
    private void NotifyLightChanges()
    {
        OnPropertyChanged(nameof(HasLights));          // Aggiorna l'UI (es. ScrollViewer)
        ConfirmCommand.NotifyCanExecuteChanged();      // Aggiorna pulsante Importa
        ClearLightsCommand.NotifyCanExecuteChanged();  // Aggiorna pulsante Svuota
    }
}