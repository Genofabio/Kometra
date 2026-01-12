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
}