using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KomaLab.Models.Fits;

namespace KomaLab.Services.Fits;

// ---------------------------------------------------------------------------
// INTERFACCIA: IFitsIoService
// RUOLO: Astrazione I/O per lettura/scrittura atomica di Header e Pixel
// ---------------------------------------------------------------------------

public interface IFitsIoService
{
    // --- LETTURA (READ) ---

    /// <summary>
    /// Legge SOLO i metadati (Header) dal file.
    /// Operazione veloce, non carica la matrice pixel.
    /// </summary>
    Task<FitsHeader?> ReadHeaderAsync(string path);

    /// <summary>
    /// Legge SOLO la matrice dei pixel (Array Raw).
    /// Esegue automaticamente il FLIP VERTICALE (Top-Down) per la visualizzazione.
    /// </summary>
    Task<Array?> ReadPixelDataAsync(string path);

    // --- SCRITTURA (WRITE) ---

    /// <summary>
    /// Aggiorna l'Header di un file esistente preservando i pixel.
    /// Implementa la logica "Safe Rewrite" (Read Pixels -> Write New Header -> Write Pixels).
    /// </summary>
    Task WriteHeaderAsync(string path, FitsHeader newHeader);

    /// <summary>
    /// Crea o sovrascrive un file FITS completo partendo da matrice e header separati.
    /// Gestisce il "Reverse Flip" (Bottom-Up) dei pixel prima del salvataggio.
    /// </summary>
    Task WriteFileAsync(string path, Array pixelData, FitsHeader header);

    // --- UTILITY BATCH (BATCH) ---

    /// <summary>
    /// Ordina una lista di file cronologicamente (basandosi su DATE-OBS nell'header).
    /// </summary>
    Task<List<string>> BatchSortByDateAsync(IEnumerable<string> paths);

    /// <summary>
    /// Valida che una lista di file abbia dimensioni compatibili (es. per Stacking).
    /// </summary>
    Task<(bool IsCompatible, string? Error)> BatchValidateAsync(IEnumerable<string> paths);
}