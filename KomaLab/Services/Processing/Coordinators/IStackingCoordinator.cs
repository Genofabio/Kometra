using System.Collections.Generic;
using System.Threading.Tasks;
using KomaLab.Models.Fits;
using KomaLab.Models.Processing;

namespace KomaLab.Services.Processing.Coordinators;

public interface IStackingCoordinator
{
    /// <summary>
    /// Coordina l'intero processo di stacking: caricamento file, 
    /// delega del calcolo all'engine, generazione metadati e salvataggio.
    /// </summary>
    Task<FitsFileReference> ExecuteStackingAsync(IEnumerable<FitsFileReference> sourceFiles, StackingMode mode);
}