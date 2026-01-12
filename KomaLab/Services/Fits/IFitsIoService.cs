using System.Collections.Generic;
using System.Threading.Tasks;
using KomaLab.Models.Fits;
using nom.tam.fits;

namespace KomaLab.Services.Fits;

// ---------------------------------------------------------------------------
// INTERFACCIA: IFitsIoService
// RUOLO: Punto di accesso unificato per l'I/O Scientifico
// DESCRIZIONE:
// Definisce il contratto per tutte le operazioni di lettura, scrittura e 
// coordinamento dei file FITS, sia per operazioni su singolo file che batch.
// ---------------------------------------------------------------------------

public interface IFitsIoService
{
    // --- OPERAZIONI SU SINGOLO FILE ---

    /// <summary>
    /// Carica un file FITS completo dal disco o risorsa.
    /// Restituisce i dati pixel raw e l'header associato.
    /// </summary>
    Task<FitsImageData?> LoadAsync(string path);

    /// <summary>
    /// Legge esclusivamente l'header FITS. 
    /// Ideale per scansioni rapide di directory o estrazione metadati senza overhead.
    /// </summary>
    Task<Header?> ReadHeaderOnlyAsync(string path);

    /// <summary>
    /// Salva i dati immagine preservando l'integrità scientifica.
    /// Gestisce automaticamente il trasferimento e la sanificazione dei metadati.
    /// </summary>
    Task SaveAsync(FitsImageData data, string path);

    // --- OPERAZIONI BATCH (Orchestrazione) ---

    /// <summary>
    /// Riceve una lista di percorsi e li restituisce ordinati cronologicamente 
    /// basandosi sui metadati temporali estratti dagli header.
    /// </summary>
    Task<List<string>> PrepareBatchAsync(IEnumerable<string> paths);

    /// <summary>
    /// Valida un insieme di file per garantire che abbiano proprietà compatibili
    /// (es. stessa risoluzione) per operazioni di Stacking o Allineamento.
    /// </summary>
    Task<(bool IsCompatible, string? Error)> ValidateCompatibilityAsync(IEnumerable<string> paths);
}