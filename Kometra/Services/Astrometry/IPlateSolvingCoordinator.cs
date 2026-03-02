using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Kometra.Models.Astrometry.Solving;
using Kometra.Models.Fits;
using Kometra.Models.Fits.Structure;

namespace Kometra.Services.Astrometry;

public interface IPlateSolvingCoordinator
{
    // Esegue la sequenza e accumula i risultati internamente
    Task SolveSequenceAsync(IEnumerable<FitsFileReference> files, IProgress<AstrometryProgressReport> progress, CancellationToken token);
    
    // Restituisce i risultati ottenuti finora (per la UI)
    IReadOnlyDictionary<FitsFileReference, FitsHeader> GetPendingResults();

    // Applica definitivamente i risultati ai file reali (Commit)
    void ApplyResults();

    // Svuota la cache senza applicare nulla (Rollback/Cancel)
    void ClearSession();
}