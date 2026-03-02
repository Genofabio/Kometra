using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Kometra.Models.Fits;
using Kometra.Models.Primitives;
using Kometra.Models.Processing;
using Kometra.Models.Processing.Alignment;
using Kometra.Models.Processing.Batch;

namespace Kometra.Services.Processing.Coordinators;

public interface IAlignmentCoordinator
{
    // --- 1. DISCOVERY ---
    Task<AlignmentTargetMetadata> GetFileMetadataAsync(FitsFileReference file);
    
    // Usato per la verifica esplicita (bottone "Verifica" o anteprima)
    Task<List<Point2D?>> DiscoverStartingPointsAsync(
        IEnumerable<FitsFileReference> files,
        AlignmentTarget target,
        string? targetName);

    // --- 2. ANALISI ---
    Task<AlignmentMap> AnalyzeSequenceAsync(
        IEnumerable<FitsFileReference> files,
        IEnumerable<Point2D?> guesses, // Coordinate provenienti dalla UI (possono essere nulle)
        AlignmentTarget target,
        AlignmentMode mode,
        CenteringMethod method,
        int searchRadius,
        string? jplTargetName = null, // Se fornito, il coordinator scarica i dati se guesses è vuoto
        IProgress<AlignmentProgressReport>? progress = null,
        CancellationToken token = default);

    // --- 3. ESECUZIONE ---
    Task<List<string>> ExecuteWarpingAsync(
        IEnumerable<FitsFileReference> files,
        AlignmentMap map,
        // Parametri di contesto per il logging e la geometria finale
        AlignmentMode mode,
        int searchRadius,
        string? jplName, 
        bool cropToCommonArea, // <--- NUOVO PARAMETRO
        IProgress<BatchProgressReport>? progress = null,
        CancellationToken token = default);
}