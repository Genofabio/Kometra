using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KomaLab.Services.Astrometry;
using KomaLab.ViewModels.Nodes;

namespace KomaLab.ViewModels.Tools;

// ---------------------------------------------------------------------------
// FILE: PlateSolvingToolViewModel.cs
// RUOLO: Orchestratore UI per Risoluzione Astrometrica
// DESCRIZIONE:
// Gestisce il ciclo di vita della sessione di Plate Solving.
// 
// RESPONSABILITÀ:
// 1. Controllo Flusso: Avvio, cancellazione e finalizzazione del batch.
// 2. Feedback Visivo: Aggiornamento log, progress bar e stati colore.
// 3. Thread Safety: Marshalling dei log provenienti dal processo esterno su UI Thread.
// 4. Decoupling: Non conosce i dettagli dei file FITS, delega tutto al Service.
// ---------------------------------------------------------------------------

public partial class PlateSolvingToolViewModel : ObservableObject
{
    // --- Dipendenze & Stato ---
    private readonly ImageNodeViewModel _targetNode;
    private readonly IPlateSolvingService _solverService;
    private CancellationTokenSource? _cts;

    // --- Risorse Statiche (Pennelli) ---
    private static readonly IBrush SuccessBrush = new SolidColorBrush(Color.Parse("#03A077"));
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#E6606A"));
    private static readonly IBrush PendingBrush = new SolidColorBrush(Color.Parse("#808080"));
    private static readonly IBrush RunningTextBrush = new SolidColorBrush(Color.Parse("#E0E0E0")); 

    // --- Proprietà Observable ---
    [ObservableProperty] private string _title = "Risoluzione Astrometrica";
    [ObservableProperty] private string _targetFileName = "";
    
    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(CanInteract))]
    private bool _isBusy;
    
    [ObservableProperty] private bool _isFinished;
    
    [ObservableProperty] private string _statusText = "Pronto per l'analisi.";
    [ObservableProperty] private IBrush _statusColor = PendingBrush;
    [ObservableProperty] private string _fullLog = ""; 
    [ObservableProperty] private int _progressValue = 0;
    [ObservableProperty] private int _progressMax = 100;

    public bool CanInteract => !IsBusy;

    // --- Costruttore ---
    public PlateSolvingToolViewModel(ImageNodeViewModel targetNode, IPlateSolvingService solverService)
    {
        _targetNode = targetNode ?? throw new ArgumentNullException(nameof(targetNode));
        _solverService = solverService ?? throw new ArgumentNullException(nameof(solverService));
        
        IsFinished = false;
        InitializeTargetName();
    }

    private void InitializeTargetName()
    {
        if (_targetNode is SingleImageNodeViewModel s) TargetFileName = Path.GetFileName(s.ImagePath);
        else if (_targetNode is MultipleImagesNodeViewModel m) TargetFileName = m.Title;
    }

    // --- Comandi ---

    [RelayCommand]
    private async Task StartSolving()
    {
        if (IsBusy) return;

        var filesToProcess = PrepareSession();
        if (filesToProcess.Count == 0) return;

        int successCount = 0;
        bool wasCancelled = false;

        try 
        {
            for (int i = 0; i < filesToProcess.Count; i++)
            {
                if (_cts!.Token.IsCancellationRequested) 
                { 
                    wasCancelled = true; 
                    break; 
                }

                bool result = await ProcessSingleFileAsync(filesToProcess[i], i, filesToProcess.Count);
                if (result) successCount++;
            }
        }
        catch (Exception ex)
        {
            AppendLog($"\n!!! ERRORE CRITICO DI SISTEMA: {ex.Message}");
        }
        finally
        {
            await FinalizeSessionAsync(successCount, filesToProcess.Count, wasCancelled);
        }
    }

    [RelayCommand]
    private void CancelOperation()
    {
        if (_cts != null && !_cts.IsCancellationRequested)
        {
            AppendLog("\n>> Arresto del processo in corso...");
            _cts.Cancel();
        }
    }

    [RelayCommand]
    private void CloseWindow(Window window) => window?.Close();

    // --- Logica Ausiliaria ---

    private List<string> PrepareSession()
    {
        IsBusy = true;
        IsFinished = false; 
        _cts = new CancellationTokenSource();
        
        FullLog = "";
        StatusColor = RunningTextBrush; 
        StatusText = "Inizializzazione...";
        ProgressValue = 0;

        var files = new List<string>();
        if (_targetNode is SingleImageNodeViewModel s) files.Add(s.ImagePath);
        else if (_targetNode is MultipleImagesNodeViewModel m) files.AddRange(m.ImagePaths);

        ProgressMax = files.Count;
        
        AppendLog($"--- INIZIO SESSIONE: {DateTime.Now:HH:mm:ss} ---");
        AppendLog($"Motore: ASTAP");
        AppendLog($"Immagini totali: {files.Count}");

        return files;
    }

    private async Task<bool> ProcessSingleFileAsync(string filePath, int index, int total)
    {
        string fileName = Path.GetFileName(filePath);
        
        StatusText = $"Risoluzione {index + 1}/{total}: {fileName}...";
        ProgressValue = index + 1;
        
        AppendLog("");
        AppendLog($"[{index + 1}/{total}] ELABORAZIONE: {fileName}");
        AppendLog("----------------------------------------");

        try
        {
            // Esecuzione del Service con callback per il log real-time
            var result = await _solverService.SolveAsync(filePath, _cts!.Token, (logLine) => 
            {
                // Dispatcher necessario per aggiornare la UI da thread esterni (processo ASTAP)
                Dispatcher.UIThread.Post(() => FullLog += logLine + Environment.NewLine);
            });

            if (result.Success)
            {
                AppendLog(">> RISULTATO: SUCCESSO");
                return true;
            }
            else
            {
                AppendLog(">> RISULTATO: FALLITO");
                
                if (!_cts.Token.IsCancellationRequested)
                {
                    // Diagnostica: Chiediamo al servizio perché il file non è stato risolto
                    // (Logica spostata dal ViewModel al Service)
                    var diagnosis = await _solverService.DiagnoseIssuesAsync(filePath);
                    AppendLog(diagnosis);
                }
                return false;
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            AppendLog($">> ERRORE DURANTE L'ESECUZIONE: {ex.Message}");
            return false;
        }
    }

    private async Task FinalizeSessionAsync(int successCount, int totalCount, bool wasCancelled)
    {
        IsBusy = false;
        _cts?.Dispose();
        _cts = null;

        if (wasCancelled)
        {
            AppendLog("\n>> SESSIONE ANNULLATA DALL'UTENTE.");
            StatusText = "Operazione interrotta.";
            StatusColor = ErrorBrush;
        }
        else
        {
            IsFinished = true;
            UpdateFinalStatus(successCount, totalCount);
        }

        // Se almeno un'immagine è stata risolta, aggiorniamo il nodo (WCS è stato scritto su disco)
        if (successCount > 0)
        {
            AppendLog("\n--- Aggiornamento Metadati Nodo ---");
            try
            {
                await _targetNode.RefreshDataFromDiskAsync();
                AppendLog("Metadati sincronizzati con successo.");
            }
            catch (Exception ex)
            {
                AppendLog($"Errore durante il refresh del nodo: {ex.Message}");
            }
        }
        
        AppendLog($"\n--- FINE SESSIONE: {DateTime.Now:HH:mm:ss} ---");
    }

    private void UpdateFinalStatus(int success, int total)
    {
        if (success == total && success > 0)
        {
            StatusText = "Risoluzione completata con successo!";
            StatusColor = SuccessBrush;
            ProgressValue = ProgressMax;
        }
        else if (success > 0)
        {
            StatusText = $"Risoluzione parziale: {success} su {total}.";
            StatusColor = PendingBrush;
        }
        else
        {
            StatusText = "Impossibile risolvere le immagini.";
            StatusColor = ErrorBrush;
        }
    }

    private void AppendLog(string message)
    {
        FullLog += message + Environment.NewLine;
    }
}