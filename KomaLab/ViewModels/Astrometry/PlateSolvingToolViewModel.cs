using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KomaLab.Models.Astrometry.Solving;
using KomaLab.Models.Fits;
using KomaLab.Models.Fits.Structure;
using KomaLab.Services.Astrometry;
using KomaLab.Services.Fits.Metadata;

namespace KomaLab.ViewModels.Astrometry;

public partial class PlateSolvingToolViewModel : ObservableObject, IDisposable
{
    private readonly IPlateSolvingCoordinator _coordinator;
    private readonly IFitsMetadataService _metadataService; 
    private readonly List<FitsFileReference> _targetFiles;
    
    private readonly StringBuilder _logBuilder = new();
    private CancellationTokenSource? _cts;
    private bool _isDisposed;

    public event Action? RequestClose;

    [ObservableProperty] private string _title = "Risoluzione Astrometrica";
    [ObservableProperty] private string _targetName = "";
    
    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(CanInteract))]
    [NotifyPropertyChangedFor(nameof(IsBusyState))]
    [NotifyPropertyChangedFor(nameof(IsInitialState))]
    [NotifyPropertyChangedFor(nameof(IsFinishedState))]
    [NotifyPropertyChangedFor(nameof(CanApply))] 
    private bool _isBusy;

    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(IsInitialState))]
    [NotifyPropertyChangedFor(nameof(IsFinishedState))]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    private PlateSolvingStatus _currentStatus = PlateSolvingStatus.Idle;

    [ObservableProperty] private string _statusText = "Pronto.";
    [ObservableProperty] private string _fullLog = ""; 
    [ObservableProperty] private int _progressValue = 0;
    [ObservableProperty] private int _progressMax = 100;

    public bool CanInteract => !IsBusy;
    public bool IsInitialState => !IsBusy && CurrentStatus == PlateSolvingStatus.Idle;
    public bool IsBusyState => IsBusy;
    public bool IsFinishedState => !IsBusy && CurrentStatus != PlateSolvingStatus.Idle;
    public bool CanApply => !IsBusy && (CurrentStatus == PlateSolvingStatus.Success || CurrentStatus == PlateSolvingStatus.PartialSuccess);

    public PlateSolvingToolViewModel(
        IEnumerable<FitsFileReference> files, 
        string targetName,
        IPlateSolvingCoordinator coordinator,
        IFitsMetadataService metadataService)
    {
        _targetFiles = files?.ToList() ?? throw new ArgumentNullException(nameof(files));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _metadataService = metadataService ?? throw new ArgumentNullException(nameof(metadataService));
        
        TargetName = targetName;
        ProgressMax = _targetFiles.Count;
        
        if (_targetFiles.Count == 0) StatusText = "Nessun file selezionato.";
        _coordinator.ClearSession();
    }

    [RelayCommand]
    private async Task StartSolving()
    {
        if (IsBusy || _targetFiles.Count == 0) return;
        
        IsBusy = true;
        CurrentStatus = PlateSolvingStatus.Running;
        _progressValue = 0;
        _coordinator.ClearSession(); 
        
        _logBuilder.Clear();
        AppendLog("=== INIZIO SESSIONE DI RISOLUZIONE ==="); 

        _cts = new CancellationTokenSource();

        // Handler che trasforma i dati in "Bellezza UI"
        var progressHandler = new Progress<AstrometryProgressReport>(ProcessProgressReport);

        try
        {
            await _coordinator.SolveSequenceAsync(_targetFiles, progressHandler, _cts.Token);
            await Task.Delay(150);
        }
        catch (OperationCanceledException) { AppendLog("\n[!] Operazione interrotta dall'utente."); }
        catch (Exception ex)
        {
            AppendLog($"\n!!! ERRORE FATALE: {ex.Message}");
            CurrentStatus = PlateSolvingStatus.Error;
            StatusText = "Errore critico durante il processo.";
        }
        finally
        {
            IsBusy = false; 
            FinalizeSession(_cts?.IsCancellationRequested ?? false);
        }
    }

    /// <summary>
    /// CUORE DEL PURISMO: Traduce i tag semantici in estetica visuale.
    /// </summary>
    private void ProcessProgressReport(AstrometryProgressReport report)
    {
        // 1. Aggiornamento Stato UI
        if (report.IsStarting)
        {
            StatusText = $"Elaborazione: {report.FileName} ({report.CurrentFileIndex}/{report.TotalFiles})";
            ProgressValue = report.CurrentFileIndex;
            AppendLog($"\n------------------------------------------------------------\n[FILE {report.CurrentFileIndex}/{report.TotalFiles}] {report.FileName}");
            return;
        }

        // 2. Parsing dei messaggi taggati
        if (string.IsNullOrEmpty(report.Message)) return;

        if (report.Message.StartsWith("CONFIG:"))
            AppendLog($"   > Config: {report.Message.Substring(7)}");

        else if (report.Message.StartsWith("TOOL:"))
            AppendLog($"     | {report.Message.Substring(5)}");

        else if (report.Message.StartsWith("SKIP:"))
            AppendLog($"   [!] SALTATO: Dati mancanti ({report.Message.Substring(5)})");

        else if (report.Message == "STATUS:SUCCESS")
        {
            // Il ViewModel decide come mostrare i dati WCS
            if (report.Result?.SolvedHeader != null)
                AppendLog(FormatWcsDetails(report.Result.SolvedHeader));
            
            AppendLog("   >>> RISOLTO CON SUCCESSO");
        }

        else if (report.Message.StartsWith("STATUS:FAIL:"))
            AppendLog($"   >>> FALLITO: {report.Message.Substring(12)}");

        else if (report.Message.StartsWith("SUMMARY:"))
        {
            var parts = report.Message.Split(':');
            AppendLog($"\n============================================================\nSESSIONE COMPLETATA\nSuccessi: {parts[1]} su {parts[2]} files.\n============================================================");
        }
        else if (report.Message.StartsWith("SYSTEM_ERROR:"))
            AppendLog($"   [!] ERRORE SISTEMA: {report.Message.Substring(13)}");
    }

    /// <summary>
    /// Crea la tabella WCS. Spostata qui per togliere responsabilità al Service.
    /// </summary>
    private string FormatWcsDetails(FitsHeader header)
    {
        var sb = new StringBuilder();
        var wcsKeys = new[] { "CTYPE1", "CTYPE2", "CRVAL1", "CRVAL2", "CD1_1", "CD1_2", "CD2_1", "CD2_2" };

        sb.AppendLine("     [NUOVI DATI WCS]");
        foreach (var key in wcsKeys)
        {
            string val = _metadataService.GetStringValue(header, key);
            if (!string.IsNullOrEmpty(val))
                sb.AppendLine($"     {key,-8} = {val}");
        }
        return sb.ToString().TrimEnd();
    }

    private void FinalizeSession(bool cancelled)
    {
        if (cancelled)
        {
            StatusText = "Operazione annullata.";
            CurrentStatus = PlateSolvingStatus.Cancelled;
        }
        else
        {
            int actualSuccesses = _coordinator.GetPendingResults().Count;
            CurrentStatus = actualSuccesses == _targetFiles.Count ? PlateSolvingStatus.Success 
                          : actualSuccesses > 0 ? PlateSolvingStatus.PartialSuccess 
                          : PlateSolvingStatus.Failed;

            StatusText = actualSuccesses > 0 
                ? $"Sessione conclusa ({actualSuccesses}/{_targetFiles.Count} risolti)."
                : "Risoluzione fallita (nessun file risolto).";
        }

        ApplyResultsCommand.NotifyCanExecuteChanged();
        StartSolvingCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private void ApplyResults()
    {
        _coordinator.ApplyResults();
        RequestClose?.Invoke();
    }

    [RelayCommand] private void Cancel() => _cts?.Cancel();
    [RelayCommand] private void Close() => RequestClose?.Invoke();

    private void AppendLog(string msg)
    {
        if (string.IsNullOrEmpty(msg)) return;
        _logBuilder.AppendLine(msg);
        FullLog = _logBuilder.ToString();
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _cts?.Cancel();
        _cts?.Dispose();
        _coordinator.ClearSession();
        _isDisposed = true;
        GC.SuppressFinalize(this);
    }
}