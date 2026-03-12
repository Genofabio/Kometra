using System.Collections.Generic;
using System.Threading.Tasks;
using Kometra.Models.Fits;

namespace Kometra.Services.Processing.Coordinators;

public interface INodeStructureCoordinator
{
    /// <summary>
    /// Unifica tutti i file forniti in un'unica sequenza, garantendo 
    /// dimensioni comuni e centratura per il Blink.
    /// </summary>
    Task<List<string>> JoinNodesAsync(List<FitsFileReference> allFiles);

    /// <summary>
    /// Restituisce i percorsi dei file per permetterne la separazione 
    /// in entità indipendenti.
    /// </summary>
    List<string> SplitNode(List<FitsFileReference> nodeFiles);
}