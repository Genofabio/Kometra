using System;
using System.Threading.Tasks;
using KomaLab.Models.Fits;
using KomaLab.Models.Fits.Structure;

namespace KomaLab.Services.Fits;

public interface IFitsDataManager
{
    // --- 1. LETTURA (Cache-Aside) ---
    Task<FitsDataPackage> GetDataAsync(string path);
    Task<FitsHeader?> GetHeaderOnlyAsync(string path);

    // --- 2. SCRITTURA E PERSISTENZA ---
    Task SaveDataAsync(string path, Array pixels, FitsHeader header);
    
    /// <summary>
    /// Crea un nuovo file temporaneo partendo da dati in memoria.
    /// </summary>
    Task<FitsFileReference> SaveAsTemporaryAsync(Array pixels, FitsHeader header, string context);

    // --- 3. GESTIONE SANDBOX E TOOL ESTERNI ---
    
    /// <summary>
    /// Crea una copia fisica di un file esistente in una zona sicura. 
    /// Il file viene tracciato per la pulizia automatica.
    /// </summary>
    Task<string> CreateSandboxCopyAsync(string originalPath, string context);

    // --- 4. MANUTENZIONE E CICLO DI VITA ---
    void Invalidate(string path);
    
    void DeleteTemporaryData(string path);
    
    /// <summary>
    /// Svuota forzatamente la cache in RAM. 
    /// I file su disco (anche temporanei) NON vengono toccati.
    /// </summary>
    void ClearCache();
    
    /// <summary>
    /// Opzione Nucleare: Svuota la RAM e DELETA tutti i file temporanei su disco.
    /// </summary>
    void Clear();
}