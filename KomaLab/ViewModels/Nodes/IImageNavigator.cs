using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;

namespace KomaLab.ViewModels.Nodes;

/// <summary>
/// Contratto per la navigazione. 
/// Include ora i comandi per permettere il binding sicuro nelle View Avalonia.
/// </summary>
public interface IImageNavigator
{
    int CurrentIndex { get; }
    int TotalCount { get; }
    bool CanMove { get; }
    
    // Stato per la visibilità/abilitazione UI
    bool CanMoveNext { get; }
    bool CanMovePrevious { get; }

    // Comandi per il binding XAML (Risolve l'errore di compilazione)
    IRelayCommand NextCommand { get; }
    IRelayCommand PreviousCommand { get; }

    // Metodi logici
    Task MoveNextAsync();
    Task MovePreviousAsync();
}