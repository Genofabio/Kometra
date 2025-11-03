using System.Collections.Generic;
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
    /// <returns>Una lista di percorsi (path) dei file selezionati, o null se l'utente annulla.</returns>
    Task<IEnumerable<string>?> ShowOpenFitsFileDialogAsync();
}