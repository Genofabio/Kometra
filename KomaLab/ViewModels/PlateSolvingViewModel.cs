using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading; // Necessario per il Dispatcher
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KomaLab.Models;
using KomaLab.Services.Astrometry;
using nom.tam.fits;

namespace KomaLab.ViewModels;

public partial class PlateSolvingViewModel : ObservableObject
{
    private readonly ImageNodeViewModel _targetNode;
    private readonly PlateSolvingService _solverService;
    private CancellationTokenSource? _cts;

    // Colori
    private static readonly IBrush SuccessBrush = new SolidColorBrush(Color.Parse("#03A077"));
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#E6606A"));
    private static readonly IBrush PendingBrush = new SolidColorBrush(Color.Parse("#808080"));
    private static readonly IBrush RunningTextBrush = new SolidColorBrush(Color.Parse("#E0E0E0")); 

    [ObservableProperty] private string _title = "Risoluzione Astrometrica";
    [ObservableProperty] private string _targetFileName = "";
    
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isFinished;
    
    [ObservableProperty] private string _statusText = "Pronto per l'analisi.";
    [ObservableProperty] private IBrush _statusColor = PendingBrush;
    [ObservableProperty] private string _fullLog = ""; 
    [ObservableProperty] private int _progressValue = 0;
    [ObservableProperty] private int _progressMax = 100;

    public PlateSolvingViewModel(ImageNodeViewModel targetNode)
    {
        _targetNode = targetNode;
        _solverService = new PlateSolvingService();
        IsFinished = false;

        if (_targetNode is SingleImageNodeViewModel s) TargetFileName = System.IO.Path.GetFileName(s.ImagePath);
        else if (_targetNode is MultipleImagesNodeViewModel m) TargetFileName = m.Title; 
    }

    [RelayCommand]
    private async Task StartSolving()
    {
        if (IsBusy) return;

        IsBusy = true;
        IsFinished = false; 
        _cts = new CancellationTokenSource();
        
        FullLog = "";
        StatusColor = RunningTextBrush; 
        StatusText = "Elaborazione in corso...";
        ProgressValue = 0;

        var filesToProcess = new List<string>();
        if (_targetNode is SingleImageNodeViewModel s) filesToProcess.Add(s.ImagePath);
        else if (_targetNode is MultipleImagesNodeViewModel m) filesToProcess.AddRange(m.ImagePaths);

        ProgressMax = filesToProcess.Count;
        int successCount = 0;
        var sb = new StringBuilder(); // Usiamo SB per costruire il log storico, ma visualizziamo anche in diretta
        bool wasCancelled = false;

        AppendLog($"--- INIZIO SESSIONE: {DateTime.Now:HH:mm:ss} ---");
        AppendLog($"Target: {filesToProcess.Count} immagini.");
        AppendLog("Motore: ASTAP");

        try 
        {
            for (int i = 0; i < filesToProcess.Count; i++)
            {
                if (_cts.Token.IsCancellationRequested) { wasCancelled = true; break; }

                string filePath = filesToProcess[i];
                string fileName = System.IO.Path.GetFileName(filePath);
                
                StatusText = $"Elaborazione {i + 1}/{filesToProcess.Count}: {fileName}...";
                
                AppendLog("");
                AppendLog($"========================================");
                AppendLog($"FILE [{i + 1}/{filesToProcess.Count}]: {fileName}");
                AppendLog($"========================================");
                AppendLog("Avvio ASTAP (Blind Solve 180°)..."); 
                
                // --- CHIAMATA CON STREAMING LOG ---
                var result = await _solverService.SolveAsync(filePath, _cts.Token, (liveLine) => 
                {
                    // Questa funzione viene chiamata dal Service ogni volta che ASTAP parla.
                    // Dobbiamo usare Dispatcher.UIThread perché siamo in un thread background.
                    Dispatcher.UIThread.Post(() => 
                    {
                        FullLog += liveLine + Environment.NewLine;
                    });
                });

                if (result.Success)
                {
                    successCount++;
                    AppendLog(">> STATO: OK");
                }
                else
                {
                    if (_cts.Token.IsCancellationRequested) { wasCancelled = true; break; }

                    AppendLog(">> STATO: FALLITO");
                    
                    var diagnosis = await Task.Run(() => AnalyzeFailureReason(filePath));
                    if (!string.IsNullOrEmpty(diagnosis)) AppendLog(diagnosis);
                }

                ProgressValue = i + 1;
            }
        }
        catch (OperationCanceledException)
        {
            wasCancelled = true;
        }
        finally
        {
            IsBusy = false;
            _cts.Dispose();
            _cts = null;

            if (wasCancelled)
            {
                AppendLog("\n>> OPERAZIONE INTERROTTA DALL'UTENTE.");
                IsFinished = false; 
                StatusText = "Operazione annullata.";
                StatusColor = ErrorBrush;
            }
            else
            {
                IsFinished = true;
                if (successCount == filesToProcess.Count && successCount > 0)
                {
                    StatusText = "Tutte le immagini risolte con successo!";
                    StatusColor = SuccessBrush;
                }
                else if (successCount > 0)
                {
                    StatusText = $"Completato parzialmente ({successCount}/{filesToProcess.Count}).";
                    StatusColor = PendingBrush; 
                }
                else
                {
                    StatusText = "Nessuna soluzione trovata.";
                    StatusColor = ErrorBrush;
                }
            }

            AppendLog("");
            AppendLog("--- Aggiornamento Dati ---");
            try
            {
                await _targetNode.RefreshDataFromDiskAsync();
                AppendLog("Fatto.");
            }
            catch (Exception ex)
            {
                AppendLog($"Errore refresh: {ex.Message}");
            }
        }
    }

    // Helper per aggiungere log in modo thread-safe (usato dal ViewModel stesso)
    private void AppendLog(string message)
    {
        FullLog += message + Environment.NewLine;
    }

    [RelayCommand] private void CancelOperation() => _cts?.Cancel();
    [RelayCommand] private void CloseWindow(Window window) => window?.Close();

    private string AnalyzeFailureReason(string filePath)
    {
        // ... (Logica identica a prima) ...
        var issues = new List<string>();
        Fits f = null;
        try
        {
            f = new Fits(filePath);
            var hdu = f.ReadHDU();
            var header = hdu.Header;
            bool hasRa = header.ContainsKey("RA") || header.ContainsKey("OBJCTRA");
            bool hasFocal = header.ContainsKey("FOCALLEN") || header.ContainsKey("FOCAL");
            bool hasPixel = header.ContainsKey("XPIXSZ") || header.ContainsKey("PIXSIZE");
            bool hasScale = header.ContainsKey("PLTSCALE");
            if (!hasRa) issues.Add("Coordinate RA/DEC mancanti");
            if (!hasScale) {
                if (!hasFocal) issues.Add("Lunghezza Focale mancante");
                if (!hasPixel) issues.Add("Dimensione Pixel mancante");
            }
        }
        catch { return ">> DIAGNOSI:\n   - Errore lettura Header"; }
        finally { f?.Close(); }

        if (issues.Count > 0) {
            var sb = new StringBuilder();
            sb.AppendLine(">> DIAGNOSI:");
            foreach (var issue in issues) sb.AppendLine($"   - {issue}");
            return sb.ToString().TrimEnd(); // Trim per evitare doppi a capo
        }
        return ">> DIAGNOSI:\n   - Soluzione non trovata";
    }
}