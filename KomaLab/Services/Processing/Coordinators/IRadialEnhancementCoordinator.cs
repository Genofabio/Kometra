using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KomaLab.Models.Fits;
using KomaLab.Models.Fits.Structure;
using KomaLab.Models.Processing;
using KomaLab.Models.Processing.Enhancement;
using KomaLab.Services.Processing.Batch;

namespace KomaLab.Services.Processing.Coordinators;

public interface IRadialEnhancementCoordinator
{
    /// <summary>
    /// Recupera l'header del file (dalla cache di sessione o dal disco).
    /// </summary>
    Task<FitsHeader> GetFileMetadataAsync(FitsFileReference file);

    /// <summary>
    /// Esegue il calcolo matematico su un singolo file e restituisce l'array di pixel grezzi (Float).
    /// Questo array verrà usato dal ViewModel per istanziare un NUOVO Renderer.
    /// NON salva nulla su disco.
    /// </summary>
    Task<Array> CalculatePreviewDataAsync(
        FitsFileReference sourceFile,
        RadialEnhancementMode mode,
        int nRad,
        int nTheta,
        double rejSig,
        double nSig);

    /// <summary>
    /// Esegue l'elaborazione batch su una lista di file, salvando i risultati su disco.
    /// </summary>
    Task<List<string>> ExecuteEnhancementAsync(
        IEnumerable<FitsFileReference> sourceFiles,
        RadialEnhancementMode mode,
        int nRad,
        int nTheta,
        double rejSig,
        double nSig,
        IProgress<BatchProgressReport>? progress = null,
        CancellationToken token = default);
}