using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KomaLab.Services.Astrometry;
using KomaLab.ViewModels.Nodes;
using nom.tam.fits;
// FONDAMENTALE per vedere ImageNodeViewModel

namespace KomaLab.ViewModels.Tools;

// ---------------------------------------------------------------------------
// FILE: PlateSolvingToolViewModel.cs
// DESCRIZIONE:
// ViewModel dedicato alla finestra di dialogo per il Plate Solving (Risoluzione Astrometrica).
//
// PATTERN & RESPONSABILITÀ:
// 1. Orchestrator: Gestisce il ciclo di vita del processo batch (Start -> Processing -> End).
// 2. Observer: Osserva l'output del Service (via callback) e aggiorna il Log in tempo reale.
// 3. Error Handling: Isola i fallimenti dei singoli file per non bloccare l'intero batch.
// 4. Thread Safety: Marshalling degli aggiornamenti UI (Log) sul Dispatcher principale.
// ---------------------------------------------------------------------------

public partial class PlateSolvingToolViewModel : ObservableObject
{
    // --- Dipendenze & Stato ---
    private readonly ImageNodeViewModel _targetNode;
    private readonly PlateSolvingService _solverService;
    private CancellationTokenSource? _cts;

    // --- Risorse Statiche (Flyweight per i pennelli) ---
    private static readonly IBrush SuccessBrush = new SolidColorBrush(Color.Parse("#03A077"));
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#E6606A"));
    private static readonly IBrush PendingBrush = new SolidColorBrush(Color.Parse("#808080"));
    private static readonly IBrush RunningTextBrush = new SolidColorBrush(Color.Parse("#E0E0E0")); 

    // --- Proprietà Observable (MVVM) ---
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

    // Proprietà calcolata per abilitare/disabilitare controlli UI
    public bool CanInteract => !IsBusy;

    public PlateSolvingToolViewModel(ImageNodeViewModel targetNode)
    {
        _targetNode = targetNode;
        // In un'architettura DI completa, questo verrebbe iniettato nel costruttore
        _solverService = new PlateSolvingService(); 
        
        IsFinished = false;
        InitializeTargetName();
    }

    private void InitializeTargetName()
    {
        if (_targetNode is SingleImageNodeViewModel s) TargetFileName = Path.GetFileName(s.ImagePath);
        else if (_targetNode is MultipleImagesNodeViewModel m) TargetFileName = m.Title;
    }

    // --- Comandi (Command Pattern) ---

    [RelayCommand]
    private async Task StartSolving()
    {
        if (IsBusy) return;

        // 1. Setup Iniziale
        var filesToProcess = PrepareSession();
        if (filesToProcess.Count == 0) return;

        int successCount = 0;
        bool wasCancelled = false;

        try 
        {
            // 2. Esecuzione Batch
            for (int i = 0; i < filesToProcess.Count; i++)
            {
                if (_cts!.Token.IsCancellationRequested) { wasCancelled = true; break; }

                bool result = await ProcessSingleFileAsync(filesToProcess[i], i, filesToProcess.Count);
                if (result) successCount++;
            }
        }
        catch (Exception ex)
        {
            // Catch-all per errori imprevisti di orchestrazione
            AppendLog($"!!! ERRORE FATALE: {ex.Message}");
        }
        finally
        {
            // 3. Finalizzazione
            await FinalizeSessionAsync(successCount, filesToProcess.Count, wasCancelled);
        }
    }

    [RelayCommand]
    private void CancelOperation()
    {
        if (_cts != null && !_cts.IsCancellationRequested)
        {
            AppendLog(">> Richiesta di cancellazione inviata...");
            _cts.Cancel();
        }
    }

    [RelayCommand]
    private void CloseWindow(Window window) => window?.Close();

    // --- Logica di Business (Private Methods) ---

    private List<string> PrepareSession()
    {
        IsBusy = true;
        IsFinished = false; 
        _cts = new CancellationTokenSource();
        
        FullLog = "";
        StatusColor = RunningTextBrush; 
        StatusText = "Preparazione sessione...";
        ProgressValue = 0;

        var files = new List<string>();
        if (_targetNode is SingleImageNodeViewModel s) files.Add(s.ImagePath);
        else if (_targetNode is MultipleImagesNodeViewModel m) files.AddRange(m.ImagePaths);

        ProgressMax = files.Count;
        
        AppendLog($"--- INIZIO SESSIONE: {DateTime.Now:HH:mm:ss} ---");
        AppendLog($"Target: {files.Count} immagini.");
        AppendLog("Motore: ASTAP (External CLI)");

        return files;
    }

    private async Task<bool> ProcessSingleFileAsync(string filePath, int index, int total)
    {
        string fileName = Path.GetFileName(filePath);
        
        // Update UI Context
        StatusText = $"Elaborazione {index + 1}/{total}: {fileName}...";
        ProgressValue = index + 1;
        
        AppendLog("");
        AppendLog($"[{index + 1}/{total}] FILE: {fileName}");
        AppendLog("----------------------------------------");

        try
        {
            // Esecuzione Service
            var result = await _solverService.SolveAsync(filePath, _cts!.Token, (logLine) => 
            {
                // Thread Marshalling: Assicura che l'aggiornamento avvenga sul UI Thread
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
                    // Diagnostica (eseguita in background per non bloccare UI)
                    var diagnosis = await Task.Run(() => AnalyzeFailureReason(filePath));
                    AppendLog(diagnosis);
                }
                return false;
            }
        }
        catch (OperationCanceledException)
        {
            throw; // Rilancia per essere gestito dal ciclo principale
        }
        catch (Exception ex)
        {
            AppendLog($">> ERRORE FILE: {ex.Message}");
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
            AppendLog("\n>> OPERAZIONE INTERROTTA DALL'UTENTE.");
            StatusText = "Operazione annullata.";
            StatusColor = ErrorBrush;
        }
        else
        {
            IsFinished = true;
            DetermineFinalStatus(successCount, totalCount);
        }

        // Aggiornamento Dati Nodo
        if (successCount > 0)
        {
            AppendLog("\n--- Aggiornamento Dati Nodo ---");
            try
            {
                await _targetNode.RefreshDataFromDiskAsync();
                AppendLog("Dati aggiornati correttamente.");
            }
            catch (Exception ex)
            {
                AppendLog($"Errore refresh: {ex.Message}");
            }
        }
    }

    private void DetermineFinalStatus(int success, int total)
    {
        if (success == total && success > 0)
        {
            StatusText = "Tutte le immagini risolte!";
            StatusColor = SuccessBrush;
            ProgressValue = ProgressMax;
        }
        else if (success > 0)
        {
            StatusText = $"Completato parzialmente ({success}/{total}).";
            StatusColor = PendingBrush;
        }
        else
        {
            StatusText = "Nessuna soluzione trovata.";
            StatusColor = ErrorBrush;
        }
    }

    private void AppendLog(string message)
    {
        FullLog += message + Environment.NewLine;
    }

    // --- Helper Diagnostico (Logica pura) ---

    private string AnalyzeFailureReason(string filePath)
    {
        var issues = new List<string>();
        Fits? f = null;
        try
        {
            f = new Fits(filePath);
            var hdu = f.ReadHDU();
            if (hdu == null) return ">> DIAGNOSI: File FITS corrotto/vuoto.";

            var header = hdu.Header;
            
            bool hasRa = header.ContainsKey("RA") || header.ContainsKey("OBJCTRA");
            bool hasDec = header.ContainsKey("DEC") || header.ContainsKey("OBJCTDEC");
            bool hasFocal = header.ContainsKey("FOCALLEN") || header.ContainsKey("FOCAL");
            bool hasPixel = header.ContainsKey("XPIXSZ") || header.ContainsKey("PIXSIZE");
            bool hasScale = header.ContainsKey("PLTSCALE"); 

            if (!hasRa || !hasDec) issues.Add("Coordinate approssimative (RA/DEC) mancanti.");
            if (!hasScale) 
            {
                if (!hasFocal) issues.Add("Lunghezza Focale mancante.");
                if (!hasPixel) issues.Add("Dimensione Pixel mancante.");
            }
        }
        catch (Exception ex)
        { 
            return $">> DIAGNOSI IMPOSSIBILE: {ex.Message}"; 
        }
        finally 
        { 
            f?.Close(); 
        }

        if (issues.Count > 0) {
            var sb = new StringBuilder();
            sb.AppendLine(">> DIAGNOSI PROBABILE:");
            foreach (var issue in issues) sb.AppendLine($"   - {issue}");
            return sb.ToString().TrimEnd();
        }
        
        return ">> DIAGNOSI: Parametri ok, possibile mancanza di stelle o nuvole.";
    }
}