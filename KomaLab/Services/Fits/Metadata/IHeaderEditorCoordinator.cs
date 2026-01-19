using System.Threading.Tasks;
using KomaLab.Models.Fits;
using KomaLab.Models.Fits.Structure;

namespace KomaLab.Services.Fits.Metadata;

public interface IHeaderEditorCoordinator
{
    // Restituisce l'header (dal sandbox se modificato, altrimenti originale)
    Task<FitsHeader?> GetHeaderAsync(FitsFileReference file);
    
    // Inserisce una nuova versione dell'header nel sandbox
    void SaveToBuffer(FitsFileReference file, FitsHeader header);
    
    // Applica tutti gli header nel sandbox ai riferimenti reali
    void CommitAll();
    
    // Svuota il sandbox senza salvare
    void ClearSession();

    // Verifica se ci sono header nel sandbox
    bool HasChanges { get; }
}