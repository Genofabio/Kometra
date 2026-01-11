using System;
using System.Threading;
using System.Threading.Tasks;
using KomaLab.Models.Astrometry;

namespace KomaLab.Services.Astrometry;

// ---------------------------------------------------------------------------
// FILE: IPlateSolvingService.cs
// DESCRIZIONE:
// Interfaccia che definisce il contratto per i servizi di risoluzione astrometrica (Plate Solving).
// Permette di disaccoppiare la logica di business dall'implementazione specifica (es. ASTAP, local/remote).
// ---------------------------------------------------------------------------

public interface IPlateSolvingService
{
    /// <summary>
    /// Avvia il processo di risoluzione delle coordinate per un'immagine FITS.
    /// </summary>
    /// <param name="fitsFilePath">Percorso completo del file FITS.</param>
    /// <param name="token">Token per la cancellazione dell'operazione.</param>
    /// <param name="onLogReceived">Callback opzionale per ricevere i log in tempo reale.</param>
    /// <returns>Oggetto risultato contenente stato (Success/Fail) e log.</returns>
    Task<PlateSolvingResult> SolveAsync(
        string fitsFilePath, 
        CancellationToken token = default, 
        Action<string>? onLogReceived = null);
}