using Avalonia.Controls;
using Avalonia.Input;
using Kometra.ViewModels;

namespace Kometra.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnGlobalKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space)
        {
            // Otteniamo l'elemento che ha il focus tramite il TopLevel (metodo raccomandato per Avalonia 11)
            var focusElement = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();

            // Se l'utente sta scrivendo dentro una TextBox, interrompiamo qui.
            // In questo modo la barra spaziatrice funzionerà normalmente digitando lo spazio nel testo.
            if (focusElement is TextBox)
            {
                return;
            }

            // Se non stiamo digitando, proviamo ad eseguire il comando di animazione
            if (DataContext is MainWindowViewModel vm)
            {
                if (vm.BoardVm.ToggleNodeAnimationCommand.CanExecute(null))
                {
                    vm.BoardVm.ToggleNodeAnimationCommand.Execute(null);
                    e.Handled = true; // Segnaliamo al sistema che questo tasto è stato gestito
                }
            }
        }
    }
}