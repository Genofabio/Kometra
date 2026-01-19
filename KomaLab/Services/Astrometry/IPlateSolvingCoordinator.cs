using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KomaLab.Models.Astrometry.Solving;
using KomaLab.Models.Fits;
using KomaLab.Models.Fits.Structure;

namespace KomaLab.Services.Astrometry;

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