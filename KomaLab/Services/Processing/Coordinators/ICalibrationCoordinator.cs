using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KomaLab.Models.Processing;

namespace KomaLab.Services.Processing.Coordinators;

public interface ICalibrationCoordinator
{
    /// <summary>
    /// Esegue la calibrazione completa: crea i Master in RAM, calibra i Light 
    /// e salva i risultati in file temporanei.
    /// </summary>
    Task<List<string>> ExecuteCalibrationAsync(
        IEnumerable<string> lightPaths,
        IEnumerable<string> darkPaths,
        IEnumerable<string> flatPaths,
        IEnumerable<string> biasPaths,
        IProgress<BatchProgressReport>? progress = null,
        CancellationToken token = default);
}