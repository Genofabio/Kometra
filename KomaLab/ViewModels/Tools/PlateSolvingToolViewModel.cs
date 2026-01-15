using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KomaLab.Models.Fits;
using KomaLab.Services.Astrometry;
using KomaLab.ViewModels.Nodes;

namespace KomaLab.ViewModels.Tools;

// ---------------------------------------------------------------------------
// FILE: PlateSolvingToolViewModel.cs
// RUOLO: Orchestratore UI per Risoluzione Astrometrica (Versione RAM-Only)
// DESCRIZIONE:
// Gestisce il ciclo di vita della sessione di Plate Solving.
// Applica i risultati WCS alla proprietà UnsavedHeader dei riferimenti file.
// ---------------------------------------------------------------------------

public partial class PlateSolvingToolViewModel : ObservableObject
{
    // --- Dipendenze ---
    private readonly ImageNodeViewModel _targetNode;
    private readonly IPlateSolvingService _solverService;
    private CancellationTokenSource? _cts;

    // --- Risorse Statiche ---
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
        else if (_targetNode is MultipleImagesNodeViewModel m) TargetFileName = m.CurrentImageText;
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
            // Otteniamo la collezione completa per applicare i risultati
            var collection = _targetNode.OutputCollection;

            for (int i = 0; i < filesToProcess.Count; i++)
            {
                if (_cts!.Token.IsCancellationRequested) 
                { 
                    wasCancelled = true; 
                    break; 
                }

                // Esecuzione del singolo file
                // Passiamo anche l'indice per recuperare il riferimento corretto dalla collezione
                bool result = await ProcessSingleFileAsync(filesToProcess[i], i, filesToProcess.Count, collection);
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

        var files = _targetNode.GetManagedFilePaths();

        ProgressMax = files.Count;
        
        AppendLog($"--- INIZIO SESSIONE: {DateTime.Now:HH:mm:ss} ---");
        AppendLog($"Motore: ASTAP (Sandbox Mode)");
        AppendLog($"Immagini totali: {files.Count}");

        return files;
    }

    private async Task<bool> ProcessSingleFileAsync(string filePath, int index, int total, FitsCollection? collection)
    {
        string fileName = Path.GetFileName(filePath);
        StatusText = $"Risoluzione {index + 1}/{total}: {fileName}...";
        ProgressValue = index + 1;

        var localLog = new StringBuilder();
        localLog.AppendLine("");
        localLog.AppendLine($"[{index + 1}/{total}] ELABORAZIONE: {fileName}");
        localLog.AppendLine("----------------------------------------");

        // 1. DIAGNOSI
        var metadataStatus = await _solverService.DiagnoseIssuesAsync(filePath);
        localLog.AppendLine($">> Stato Header: {metadataStatus}");

        try
        {
            var result = await _solverService.SolveAsync(filePath, _cts!.Token, (logLine) => 
            {
                localLog.AppendLine(logLine);
            });

            if (result.Success && result.SolvedHeader != null)
            {
                // AGGIORNAMENTO STATO IN MEMORIA
                if (collection != null && index < collection.Count)
                {
                    // Troviamo il riferimento al file nella collezione
                    var fileRef = collection[index];
                    
                    // Salviamo l'header risolto come "Modifica non salvata"
                    fileRef.UnsavedHeader = result.SolvedHeader;
                    
                    localLog.AppendLine(">> RISULTATO: SUCCESSO (WCS applicato in memoria)");
                }
                else
                {
                    localLog.AppendLine(">> RISULTATO: SUCCESSO (ma riferimento file perso)");
                }
            
                AppendLog(localLog.ToString());
                return true;
            }
            else
            {
                localLog.AppendLine(">> RISULTATO: FALLITO");
                if (!result.FullLog.Contains("Exception") && !_cts!.Token.IsCancellationRequested)
                    localLog.AppendLine(">> Nota: Immagine non risolta (stelle insufficienti o parametri errati).");

                AppendLog(localLog.ToString());
                return false;
            }
        }
        catch (Exception ex)
        {
            localLog.AppendLine($">> ERRORE SISTEMA: {ex.Message}");
            AppendLog(localLog.ToString());
            return false;
        }
    }

    private Task FinalizeSessionAsync(int successCount, int totalCount, bool wasCancelled)
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
            
            if (successCount > 0)
            {
                AppendLog("\n--- OPERAZIONE COMPLETATA ---");
                AppendLog("Le coordinate celesti (WCS) sono ora attive in memoria.");
                // Nota per l'utente: le modifiche sono nell'UnsavedHeader
                AppendLog("ATTENZIONE: Le modifiche sono temporanee. Usa 'Salva' sul nodo per renderle permanenti.");
            }
        }
        
        // Notifica il nodo che i dati sono cambiati (opzionale, per refresh UI immediato)
        // Ma siccome il WCS non cambia l'immagine visibile, non serve ricaricare i pixel.
        
        AppendLog($"\n--- FINE SESSIONE: {DateTime.Now:HH:mm:ss} ---");
        return Task.CompletedTask;
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