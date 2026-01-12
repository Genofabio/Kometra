using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace KomaLab.ViewModels;

// ---------------------------------------------------------------------------
// FILE: MainWindowViewModel.cs
// RUOLO: Root ViewModel
// DESCRIZIONE:
// ViewModel radice della finestra principale.
// Funge da contenitore per il BoardViewModel (il cuore dell'applicazione)
// e gestisce le proprietà globali della finestra (es. Titolo, Chiusura).
// ---------------------------------------------------------------------------

public partial class MainWindowViewModel : ObservableObject
{
    // --- Sotto-ViewModel Principale ---
    public BoardViewModel BoardVm { get; }
    
    // --- Proprietà della Finestra ---
    [ObservableProperty]
    private string _windowTitle = "KomaLab - Astro Node Editor";

    // --- Costruttore ---
    // BoardViewModel viene iniettato dal container DI (App.axaml.cs o Program.cs)
    public MainWindowViewModel(BoardViewModel boardVm)
    {
        BoardVm = boardVm;
        PrintProjectStructure();
    }
    
    // --- Comandi Globali ---
    [RelayCommand]
    private void ExitApp()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }
    
    private void PrintProjectStructure()
    {
        try 
        {
            // Risaliamo dalla cartella /bin/Debug/net9.0 fino alla root del progetto
            string executionPath = AppContext.BaseDirectory;
            DirectoryInfo? directory = new DirectoryInfo(executionPath);
            
            // Risaliamo finché non troviamo la cartella "Models" o finché non finiscono i genitori
            while (directory != null && !directory.GetDirectories("Models").Any())
            {
                directory = directory.Parent;
            }

            if (directory == null) return;

            Debug.WriteLine("\n" + new string('=', 50));
            Debug.WriteLine("DISEGNO ARCHITETTURALE KOMALAB");
            Debug.WriteLine(new string('=', 50));

            string[] targetFolders = { "Models", "Services", "ViewModels" };

            foreach (var folderName in targetFolders)
            {
                var targetDir = directory.GetDirectories(folderName).FirstOrDefault();
                if (targetDir != null)
                {
                    Debug.WriteLine($"\n[{targetDir.Name.ToUpper()}]");
                    PrintDirectory(targetDir, "");
                }
            }
            Debug.WriteLine("\n" + new string('=', 50) + "\n");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Errore nel print della struttura: {ex.Message}");
        }
    }

    private void PrintDirectory(DirectoryInfo dir, string indent)
    {
        // Ottieni cartelle e file (escludendo bin/obj)
        var subDirs = dir.GetDirectories().Where(d => d.Name != "bin" && d.Name != "obj").ToList();
        var files = dir.GetFiles("*.cs").Concat(dir.GetFiles("*.axaml")).ToList();
        
        var allItems = subDirs.Cast<FileSystemInfo>().Concat(files.Cast<FileSystemInfo>()).ToList();

        for (int i = 0; i < allItems.Count; i++)
        {
            bool isLast = i == allItems.Count - 1;
            var item = allItems[i];
            string prefix = isLast ? "└── " : "├── ";

            if (item is DirectoryInfo subDir)
            {
                Debug.WriteLine($"{indent}{prefix}[{subDir.Name}]");
                PrintDirectory(subDir, indent + (isLast ? "    " : "│   "));
            }
            else
            {
                Debug.WriteLine($"{indent}{prefix}{item.Name}");
            }
        }
    }
}