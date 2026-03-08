using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Kometra.Models.Fits;
using Kometra.Models.Export;
using Kometra.Models.Fits.Structure; // Import necessario per FitsCompressionMode

namespace Kometra.Services.Fits.IO;

/// <summary>
/// Servizio di I/O di basso livello per il formato FITS.
/// Si occupa della traduzione tra file fisico e oggetti di memoria, 
/// supportando strutture a singolo HDU, Multi-Extension (MEF) e compressione Tile.
/// </summary>
public interface IFitsIoService
{
    // --- LETTURA (READ) ---

    /// <summary>
    /// Legge tutti gli HDU presenti nel file (Primario ed Estensioni).
    /// Gestisce internamente l'ereditarietà dei metadati e la decompressione (RICE, GZIP, Deflate).
    /// </summary>
    Task<List<FitsHdu>> ReadAllHdusAsync(string path);

    /// <summary>
    /// Legge i metadati della prima immagine valida (non vuota) trovata nel file.
    /// </summary>
    Task<FitsHeader?> ReadHeaderAsync(string path);

    /// <summary>
    /// Legge la matrice dei pixel della prima immagine valida. 
    /// Restituisce l'array già flippato verticalmente (Top-Down).
    /// </summary>
    Task<Array?> ReadPixelDataAsync(string path);

    // --- SCRITTURA (WRITE) ---

    /// <summary>
    /// Crea un file FITS standard. 
    /// Se viene specificata una modalità di compressione, il file viene salvato come BINTABLE compressa.
    /// </summary>
    Task WriteFileAsync(string path, Array pixelData, FitsHeader header, FitsCompressionMode mode = FitsCompressionMode.None);

    /// <summary>
    /// Crea un file FITS multi-estensione (MEF).
    /// Applica la compressione specificata ad ogni estensione contenente dati.
    /// </summary>
    Task WriteMergedFileAsync(string path, List<(Array Pixels, FitsHeader Header)> blocks, FitsCompressionMode mode = FitsCompressionMode.None);
    
    // --- GESTIONE FILE E PATH ---
    
    /// <summary>
    /// Esegue una copia diretta da disco a disco.
    /// </summary>
    Task CopyFileAsync(string sourcePath, string destPath);
    
    /// <summary>
    /// Costruisce un percorso assoluto sicuro nella cartella temporanea di Kometra.
    /// </summary>
    string BuildRawPath(string subFolder, string fileName);
    
    /// <summary>
    /// Tenta di eliminare un file se esistente, ignorando eventuali errori di accesso.
    /// </summary>
    void TryDeleteFile(string path);
}