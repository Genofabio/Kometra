using System;
using System.Threading.Tasks;
using KomaLab.Models.Fits;
using KomaLab.Models.Fits.Structure;

namespace KomaLab.Services.Fits.IO;

/// <summary>
/// Servizio di I/O di basso livello per il formato FITS.
/// Si occupa esclusivamente della traduzione tra file fisico e oggetti di memoria.
/// </summary>
public interface IFitsIoService
{
    // --- LETTURA (READ) ---

    /// <summary>
    /// Legge solo i metadati (Header). Utile per ispezione rapida.
    /// </summary>
    Task<FitsHeader?> ReadHeaderAsync(string path);

    /// <summary>
    /// Legge la matrice dei pixel. 
    /// Restituisce l'array già flippato verticalmente per l'uso in RAM (Top-Down).
    /// </summary>
    Task<Array?> ReadPixelDataAsync(string path);

    // --- SCRITTURA (WRITE) ---

    /// <summary>
    /// Crea o sovrascrive un file FITS. 
    /// Gestisce internamente il padding di 2880 byte e il reverse flip dei dati.
    /// </summary>
    Task WriteFileAsync(string path, Array pixelData, FitsHeader header);
    
    // --- Gestione Path ---
    
    Task CopyFileAsync(string sourcePath, string destPath);
    string BuildRawPath(string subFolder, string fileName);
    void TryDeleteFile(string path);
    
}