using System.Threading.Tasks;

namespace KomaLab.Services;

/// <summary>
/// Definisce un servizio per mostrare finestre di dialogo (Apri/Salva File).
/// </summary>
public interface IDialogService
{
    /// <summary>
    /// Mostra la finestra di dialogo "Apri File" per i file FITS.
    /// </summary>
    /// <returns>Il percorso (path) del file selezionato, o null se l'utente annulla.</returns>
    Task<string?> ShowOpenFitsFileDialogAsync();
}