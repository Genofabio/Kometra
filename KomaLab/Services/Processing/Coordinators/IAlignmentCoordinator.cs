using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KomaLab.Models.Fits;
using KomaLab.Models.Primitives;
using KomaLab.Models.Processing;

namespace KomaLab.Services.Processing.Coordinators;

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
        // Aggiungi qui i parametri di contesto che vuoi loggare
        AlignmentMode mode,
        int searchRadius,
        string? jplName, 
        IProgress<BatchProgressReport>? progress = null,
        CancellationToken token = default);
}