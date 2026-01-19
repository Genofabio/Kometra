using System;
using System.Threading;
using System.Threading.Tasks;
using KomaLab.Models.Astrometry;
using KomaLab.Models.Astrometry.Solving;
using KomaLab.Models.Fits;

namespace KomaLab.Services.Astrometry;

public interface IPlateSolvingService
{
    /// <summary>
    /// Esegue una diagnosi preventiva sui metadati. 
    /// Accetta FitsFileReference per controllare sia il disco che eventuali modifiche in RAM.
    /// </summary>
    Task<AstrometryDiagnosis> DiagnoseIssuesAsync(FitsFileReference fileRef);

    /// <summary>
    /// Esegue il Plate Solving su una copia sandbox.
    /// </summary>
    Task<PlateSolvingResult> SolveFileAsync(
        FitsFileReference fileRef, 
        CancellationToken token = default, 
        IProgress<string>? liveLog = null);
}