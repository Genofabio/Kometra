using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KomaLab.Models;
using KomaLab.Services.Astrometry;

namespace KomaLab.ViewModels;

public partial class PlateSolvingViewModel : ObservableObject
{
    private readonly ImageNodeViewModel _targetNode;
    private readonly PlateSolvingService _solverService;

    // --- COLORI STATO ---
    private static readonly IBrush SuccessBrush = new SolidColorBrush(Color.Parse("#03A077")); // Verde
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#E6606A"));   // Rosso rosso
    private static readonly IBrush PendingBrush = new SolidColorBrush(Color.Parse("#808080")); // Grigio scuro
    
    // Il testo durante il caricamento deve essere una sfumatura di bianco
    private static readonly IBrush RunningTextBrush = new SolidColorBrush(Color.Parse("#E0E0E0")); 

    [ObservableProperty] private string _title = "Risoluzione Astrometrica";
    [ObservableProperty] private string _targetFileName = "";

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "Pronto per l'analisi.";
    [ObservableProperty] private IBrush _statusColor = PendingBrush;
    
    [ObservableProperty] private string _fullLog = ""; 

    [ObservableProperty] private int _progressValue = 0;
    [ObservableProperty] private int _progressMax = 100;

    public PlateSolvingViewModel(ImageNodeViewModel targetNode)
    {
        _targetNode = targetNode;
        _solverService = new PlateSolvingService();

        // Titolo pulito senza conteggi
        if (_targetNode is SingleImageNodeViewModel s) 
        {
            TargetFileName = System.IO.Path.GetFileName(s.ImagePath);
        }
        else if (_targetNode is MultipleImagesNodeViewModel m) 
        {
            TargetFileName = m.Title; 
        }
    }

    [RelayCommand]
    private async Task StartSolving()
    {
        if (IsBusy) return;

        IsBusy = true;
        FullLog = "";
        
        // Colore testo durante l'elaborazione: Sfumatura di bianco
        StatusColor = RunningTextBrush; 
        StatusText = "Inizializzazione...";
        ProgressValue = 0;

        var filesToProcess = new List<string>();
        if (_targetNode is SingleImageNodeViewModel s) filesToProcess.Add(s.ImagePath);
        else if (_targetNode is MultipleImagesNodeViewModel m) filesToProcess.AddRange(m.ImagePaths);

        ProgressMax = filesToProcess.Count;
        int successCount = 0;
        var sb = new StringBuilder();

        sb.AppendLine($"--- INIZIO SESSIONE: {DateTime.Now:HH:mm:ss} ---");
        sb.AppendLine($"Target: {filesToProcess.Count} immagini.");
        FullLog = sb.ToString();

        for (int i = 0; i < filesToProcess.Count; i++)
        {
            string filePath = filesToProcess[i];
            string fileName = System.IO.Path.GetFileName(filePath);
            
            StatusText = $"Elaborazione {i + 1}/{filesToProcess.Count}: {fileName}...";
            
            sb.AppendLine();
            sb.AppendLine($"========================================");
            sb.AppendLine($"FILE [{i + 1}/{filesToProcess.Count}]: {fileName}");
            sb.AppendLine($"========================================");
            FullLog = sb.ToString();

            var result = await _solverService.SolveAsync(filePath);

            if (!string.IsNullOrWhiteSpace(result.LogOutput))
            {
                sb.Append(result.LogOutput);
                if (!result.LogOutput.EndsWith(Environment.NewLine)) sb.AppendLine();
            }

            if (result.Success)
            {
                successCount++;
                sb.AppendLine(">> STATO: OK (WCS Aggiornato)");
            }
            else
            {
                sb.AppendLine(">> STATO: FALLITO");
            }

            FullLog = sb.ToString();
            ProgressValue = i + 1;
        }

        IsBusy = false;

        if (successCount == filesToProcess.Count)
        {
            // Messaggio ripristinato
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
            StatusText = "Fallito.";
            StatusColor = ErrorBrush;
        }

        sb.AppendLine();
        sb.AppendLine("--- Aggiornamento Dati ---");
        sb.AppendLine("Ricaricamento immagini in memoria...");
        FullLog = sb.ToString();

        try
        {
            await _targetNode.RefreshDataFromDiskAsync();
            sb.AppendLine("Fatto. I dati visualizzati sono aggiornati.");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"ERRORE RELOAD: {ex.Message}");
        }

        FullLog = sb.ToString();
    }
}