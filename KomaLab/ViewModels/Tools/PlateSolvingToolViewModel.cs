using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KomaLab.Models.Fits;
using KomaLab.Models.Processing;
using KomaLab.Services.Processing.Coordinators;
using KomaLab.ViewModels.Enums;

namespace KomaLab.ViewModels.Tools;

/// <summary>
/// ViewModel per il Tool di Plate Solving.
/// Agisce come puro osservatore del processo coordinato dall'AstrometryCoordinator.
/// </summary>
public partial class PlateSolvingToolViewModel : ObservableObject
{
    // --- Dipendenze ---
    private readonly IAstrometryCoordinator _coordinator;
    private readonly List<FitsFileReference> _targetFiles;
    
    // --- Stato Interno ---
    private readonly StringBuilder _logBuilder = new();
    private CancellationTokenSource? _cts;
    private int _successCount;

    // --- Proprietà UI ---
    [ObservableProperty] private string _title = "Risoluzione Astrometrica";
    [ObservableProperty] private string _targetName = "";
    
    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(CanInteract))]
    private bool _isBusy;
    
    [ObservableProperty] private PlateSolvingStatus _currentStatus = PlateSolvingStatus.Idle;
    [ObservableProperty] private string _statusText = "Pronto.";
    [ObservableProperty] private string _fullLog = ""; 
    [ObservableProperty] private int _progressValue = 0;
    [ObservableProperty] private int _progressMax = 100;

    public bool CanInteract => !IsBusy;
    public event Action? RequestClose;

    public PlateSolvingToolViewModel(
        IEnumerable<FitsFileReference> files, 
        string targetName,
        IAstrometryCoordinator coordinator) // Riceve il coordinatore, non più il servizio
    {
        if (files == null) throw new ArgumentNullException(nameof(files));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        
        _targetFiles = files.ToList();
        TargetName = targetName;
        ProgressMax = _targetFiles.Count;
        
        if (_targetFiles.Count == 0)
            StatusText = "Nessun file selezionato.";
    }

    // =======================================================================
    // ESECUZIONE SESSIONE
    // =======================================================================

    [RelayCommand]
    private async Task StartSolving()
    {
        if (IsBusy || _targetFiles.Count == 0) return;
        
        // 1. Setup Iniziale
        IsBusy = true;
        CurrentStatus = PlateSolvingStatus.Running;
        _successCount = 0;
        _logBuilder.Clear();
        FullLog = ""; 

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        // 2. Definizione del Reporting strutturato
        var progressHandler = new Progress<AstrometryProgressReport>(report => 
        {
            // Gestione Avanzamento e Testi
            if (report.IsStarting)
            {
                StatusText = $"[{report.CurrentFileIndex}/{report.TotalFiles}] {report.FileName}";
                ProgressValue = report.CurrentFileIndex;
            }

            // Gestione Messaggi di Log
            if (!string.IsNullOrEmpty(report.Message))
            {
                AppendLog(report.Message);
            }

            // Monitoraggio Successi
            if (report.IsCompleted && report.Success)
            {
                _successCount++;
            }
        });

        try
        {
            AppendLog("--- INIZIO SESSIONE ASTROMETRICA ---");
            AppendLog($"Target: {TargetName} | Sequenza: {_targetFiles.Count} file");

            // 3. DELEGA TOTALE: Il coordinatore gestisce diagnosi, loop e ASTAP
            await _coordinator.SolveSequenceAsync(_targetFiles, progressHandler, token);

            FinalizeSession(token.IsCancellationRequested);
        }
        catch (OperationCanceledException)
        {
            FinalizeSession(true);
        }
        catch (Exception ex)
        {
            AppendLog($"\n!!! ERRORE FATALE: {ex.Message}");
            CurrentStatus = PlateSolvingStatus.Error;
            StatusText = "Errore durante il processo.";
        }
        finally
        {
            IsBusy = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    private void FinalizeSession(bool cancelled)
    {
        if (cancelled)
        {
            StatusText = "Operazione annullata.";
            CurrentStatus = PlateSolvingStatus.Cancelled;
            AppendLog("\n--- SESSIONE INTERROTTA DALL'UTENTE ---");
        }
        else
        {
            // Determiniamo lo stato finale basandoci sui successi riportati dal coordinatore
            CurrentStatus = _successCount == _targetFiles.Count ? PlateSolvingStatus.Success 
                          : _successCount > 0 ? PlateSolvingStatus.PartialSuccess 
                          : PlateSolvingStatus.Failed;

            StatusText = $"Sessione conclusa ({_successCount}/{_targetFiles.Count} risolti).";
            AppendLog($"\n--- FINE SESSIONE: {_successCount} successi ---");
        }
    }

    private void AppendLog(string msg)
    {
        if (string.IsNullOrEmpty(msg)) return;

        // Aggiungiamo il timestamp per un look professionale se non è un messaggio vuoto
        _logBuilder.AppendLine(msg);
        
        // Aggiornamento della proprietà per il Binding XAML
        FullLog = _logBuilder.ToString();
    }
    
    // =======================================================================
    // COMANDI NAVIGAZIONE
    // =======================================================================

    [RelayCommand] 
    private void Cancel() => _cts?.Cancel();
    
    [RelayCommand] 
    private void Close() 
    {
        if (IsBusy) _cts?.Cancel();
        RequestClose?.Invoke();
    }
}