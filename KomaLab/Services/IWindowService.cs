using Avalonia.Controls;
using KomaLab.ViewModels;

namespace KomaLab.Services;

/// <summary>
/// Definisce un servizio per gestire l'apertura di finestre secondarie.
/// </summary>
public interface IWindowService
{
    /// <summary>
    /// Registra la finestra principale dell'applicazione per
    /// usarla come genitore per le finestre modali.
    /// </summary>
    void RegisterMainWindow(Window window);

    /// <summary>
    /// Apre la finestra di allineamento per il nodo specificato.
    /// </summary>
    void ShowAlignmentWindow(BaseNodeViewModel nodeToAlign);
}