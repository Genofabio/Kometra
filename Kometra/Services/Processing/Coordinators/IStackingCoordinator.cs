using System.Collections.Generic;
using System.Threading.Tasks;
using Kometra.Models.Fits;
using Kometra.Models.Processing;
using Kometra.Models.Processing.Stacking;

namespace Kometra.Services.Processing.Coordinators;

public interface IStackingCoordinator
{
    /// <summary>
    /// Coordina l'intero processo di stacking: caricamento file, 
    /// delega del calcolo all'engine, generazione metadati e salvataggio.
    /// </summary>
    Task<FitsFileReference> ExecuteStackingAsync(IEnumerable<FitsFileReference> sourceFiles, StackingMode mode);
}