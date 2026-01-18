using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KomaLab.Models.Astrometry.Solving;
using KomaLab.Models.Fits;
using KomaLab.Models.Processing;

namespace KomaLab.Services.Processing.Coordinators;

public interface IAstrometryCoordinator
{
    // Il nome del metodo chiarisce che stiamo risolvendo (solving) 
    // all'interno del dominio Astrometria.
    Task SolveSequenceAsync(
        IEnumerable<FitsFileReference> files, 
        IProgress<AstrometryProgressReport> progress, 
        CancellationToken token);
}