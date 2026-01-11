using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using KomaLab.Models.Visualization;
using KomaLab.ViewModels;
using nom.tam.fits;

namespace KomaLab.Services.UI;

// ---------------------------------------------------------------------------
// FILE: IWindowService.cs
// RUOLO: Interfaccia di Navigazione UI
// DESCRIZIONE:
// Definisce il contratto per la gestione delle finestre modali e dei tool
// secondari. Astrae la creazione delle View dai ViewModel, permettendo
// l'apertura di finestre complesse senza accoppiamento diretto.
// ---------------------------------------------------------------------------

public interface IWindowService
{
    /// <summary>
    /// Registra la finestra principale dell'applicazione per
    /// usarla come owner per le finestre modali.
    /// </summary>
    void RegisterMainWindow(Window window);

    /// <summary>
    /// Apre il tool di allineamento e stacking.
    /// </summary>
    Task<List<string>?> ShowAlignmentWindowAsync(
        List<string> inputPaths, 
        VisualizationMode initialMode = VisualizationMode.Linear);
    
    /// <summary>
    /// Apre l'editor per la modifica dei metadati FITS.
    /// </summary>
    Task<Header?> ShowHeaderEditorAsync(ImageNodeViewModel node);
    
    /// <summary>
    /// Apre il pannello di controllo per il Plate Solving (Astrometria).
    /// </summary>
    Task ShowPlateSolvingWindowAsync(ImageNodeViewModel node);
    
    /// <summary>
    /// Apre il tool per l'effetto di posterizzazione.
    /// </summary>
    Task<List<string>?> ShowPosterizationWindowAsync(
        List<string> sourcePaths, 
        VisualizationMode initialMode);
}