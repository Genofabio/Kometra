using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using KomaLab.Models;
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
    Task<List<string>?> ShowAlignmentWindowAsync(List<string> sourcePaths);
}