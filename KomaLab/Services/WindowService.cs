using Avalonia.Controls;
using KomaLab.ViewModels;
using KomaLab.Views; // <-- Aggiungi questo per la nostra finestra
using System; // <-- Aggiungi questo

namespace KomaLab.Services;

public class WindowService : IWindowService
{
    private Window? _mainWindow;

    public void RegisterMainWindow(Window window)
    {
        _mainWindow = window;
    }

    public void ShowAlignmentWindow(BaseNodeViewModel nodeToAlign)
    {
        if (_mainWindow == null)
        {
            // Non possiamo aprire una finestra modale senza un genitore
            throw new InvalidOperationException("La finestra principale non è stata registrata.");
        }

        // --- Per ora, apriamo solo una finestra vuota ---
        
        // 1. Crea la nuova finestra
        var alignmentWindow = new AlignmentWindow();

        // 2. TODO: In futuro, creeremo un AlignmentViewModel e lo imposteremo come DataContext
        // var viewModel = new AlignmentViewModel(nodeToAlign);
        // alignmentWindow.DataContext = viewModel;

        // 3. Mostra la finestra in modo "modale" (blocca la finestra principale)
        alignmentWindow.ShowDialog(_mainWindow);
    }
}