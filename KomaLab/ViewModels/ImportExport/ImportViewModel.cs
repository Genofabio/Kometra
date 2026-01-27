using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KomaLab.Models.Fits.Structure; // Necessario per FitsHdu
using KomaLab.Models.Processing;
using KomaLab.Services.Fits;         // Necessario per IFitsDataManager
using KomaLab.Services.Processing.Coordinators;
using KomaLab.Services.UI;

namespace KomaLab.ViewModels.ImportExport;

/// <summary>
/// ViewModel per la gestione dell'importazione di file FITS con calibrazione opzionale.
/// Gestisce automaticamente i file MEF (Multi-Extension FITS) separandoli in file singoli.
/// </summary>
public partial class ImportViewModel : ObservableObject
{
    private readonly IDialogService _dialogService;
    private readonly ICalibrationCoordinator _calibrationCoordinator;
    private readonly IFitsDataManager _dataManager; // <--- NUOVA DIPENDENZA

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

    // --- Risultati per la Board ---
    public List<string>? CalibratedResultPaths { get; private set; }
    public bool DialogResult { get; private set; }
    public event Action? RequestClose;

    public ImportViewModel(
        IDialogService dialogService, 
        ICalibrationCoordinator calibrationCoordinator,
        IFitsDataManager dataManager) // <--- Iniettato qui
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
                // Se è già presente, saltiamo
                if (collection.Contains(path)) continue;

                try
                {
                    // 1. Ispezioniamo il file per vedere se è un MEF (Multi-Extension)
                    var dataPackage = await _dataManager.GetDataAsync(path);
                    
                    // Contiamo quanti HDU contengono immagini valide
                    var imageHdus = dataPackage.Hdus.Where(h => !h.IsEmpty).ToList();

                    if (imageHdus.Count > 1)
                    {
                        // CASO MEF: Il file contiene più immagini. Le esplodiamo.
                        StatusText = $"Estrazione estensioni da {System.IO.Path.GetFileName(path)}...";
                        
                        for (int i = 0; i < imageHdus.Count; i++)
                        {
                            var hdu = imageHdus[i];
                            
                            // Salviamo ogni estensione come file temporaneo singolo
                            // Aggiungiamo metadati per tracciare l'origine
                            var header = hdu.Header.Clone(); // Clone per non sporcare cache
                            // Nota: NON usiamo _metadataService qui per brevità, usiamo accesso diretto se necessario
                            // o lasciamo l'header così com'è.
                            
                            var tempRef = await _dataManager.SaveAsTemporaryAsync(
                                hdu.PixelData, 
                                header, 
                                $"{contextLabel}_Ext_{i+1}");

                            collection.Add(tempRef.FilePath);
                        }
                    }
                    else
                    {
                        // CASO STANDARD: Singola immagine o Primary HDU
                        collection.Add(path);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ImportVM] Errore analisi file {path}: {ex.Message}");
                    // Fallback: lo aggiungiamo così com'è se non riusciamo a leggerlo
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

    [RelayCommand] 
    private void RemoveLight(string path) 
    {
        if (LightFiles.Remove(path)) NotifyLightChanges();
    }

    [RelayCommand] 
    private void RemoveDark(string path) 
    {
        RemoveFromCollection(DarkFiles, path);
        ClearDarksCommand.NotifyCanExecuteChanged(); 
    }

    [RelayCommand] 
    private void RemoveFlat(string path) 
    {
        RemoveFromCollection(FlatFiles, path);
        ClearFlatsCommand.NotifyCanExecuteChanged(); 
    }

    [RelayCommand] 
    private void RemoveBias(string path) 
    {
        RemoveFromCollection(BiasFiles, path);
        ClearBiasCommand.NotifyCanExecuteChanged(); 
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

    [RelayCommand(CanExecute = nameof(CanClearLights))]
    private void ClearLights()
    {
        LightFiles.Clear();
        NotifyLightChanges();
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
        OnPropertyChanged(nameof(HasLights));          
        ConfirmCommand.NotifyCanExecuteChanged();      
        ClearLightsCommand.NotifyCanExecuteChanged();  
    }
}