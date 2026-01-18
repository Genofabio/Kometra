using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KomaLab.Models.Astrometry;
using KomaLab.Models.Fits;
using KomaLab.Models.Primitives;
using KomaLab.Models.Processing;
using KomaLab.Services.Astrometry;
using KomaLab.Services.Fits;
using KomaLab.Services.Fits.Metadata;
using KomaLab.Services.Processing.Alignment;
using KomaLab.Services.Processing.Batch;
using KomaLab.Services.Processing.Engines;
using OpenCvSharp;

namespace KomaLab.Services.Processing.Coordinators;

public class AlignmentCoordinator : IAlignmentCoordinator
{
    private readonly IAlignmentService _alignmentService;
    private readonly IBatchProcessingService _batchService;
    private readonly IGeometricEngine _geometricEngine;
    private readonly IFitsDataManager _dataManager;
    private readonly IFitsMetadataService _metadataService;
    private readonly IJplHorizonsService _jplService;

    public AlignmentCoordinator(
        IAlignmentService alignmentService,
        IBatchProcessingService batchService,
        IGeometricEngine geometricEngine,
        IFitsDataManager dataManager,
        IFitsMetadataService metadataService,
        IJplHorizonsService jplService)
    {
        _alignmentService = alignmentService;
        _batchService = batchService;
        _geometricEngine = geometricEngine;
        _dataManager = dataManager;
        _metadataService = metadataService;
        _jplService = jplService;
    }

    // =======================================================================
    // 1. DISCOVERY (Metadati e Punti di partenza)
    // =======================================================================

    public async Task<AlignmentTargetMetadata> GetFileMetadataAsync(FitsFileReference file)
    {
        var header = await _dataManager.GetHeaderOnlyAsync(file.FilePath);
        if (header == null) throw new InvalidOperationException("Header non disponibile.");

        string obj = _metadataService.GetStringValue(header, "OBJECT").Replace("'", "").Trim();
        var wcs = _metadataService.ExtractWcs(header);

        return new AlignmentTargetMetadata(
            ObjectName: string.IsNullOrWhiteSpace(obj) ? "Unknown" : obj,
            ObservationDate: _metadataService.GetObservationDate(header),
            Location: _metadataService.GetObservatoryLocation(header),
            HasWcs: wcs.IsValid,
            ImageWidth: _metadataService.GetIntValue(header, "NAXIS1"),
            ImageHeight: _metadataService.GetIntValue(header, "NAXIS2")
        );
    }

    public async Task<List<Point2D?>> DiscoverStartingPointsAsync(
        IEnumerable<FitsFileReference> files,
        AlignmentTarget target,
        string? targetName)
    {
        var fileList = files.ToList();
        var results = new List<Point2D?>();

        if (target == AlignmentTarget.Stars)
        {
            var refHeader = await _dataManager.GetHeaderOnlyAsync(fileList[0].FilePath);
            var refWcs = _metadataService.ExtractWcs(refHeader!);
            
            foreach (var f in fileList)
                results.Add(await FetchWcsPointAsync(f, refWcs.RefRaDeg, refWcs.RefDecDeg));
        }
        else if (target == AlignmentTarget.Comet && !string.IsNullOrEmpty(targetName))
        {
            foreach (var f in fileList)
                results.Add(await FetchJplPointAsync(f, targetName));
        }

        return results;
    }

    // =======================================================================
    // 2. ANALISI (Centroidi e Mappa)
    // =======================================================================
    
    public async Task<AlignmentMap> AnalyzeSequenceAsync(
        IEnumerable<FitsFileReference> files,
        IEnumerable<Point2D?> guesses,
        AlignmentTarget target,
        AlignmentMode mode,
        CenteringMethod method,
        int searchRadius,
        IProgress<AlignmentProgressReport>? progress = null,
        CancellationToken token = default)
    {
        var centers = await _alignmentService.CalculateCentersAsync(
            target, mode, method, files.ToList(), guesses.ToList(), searchRadius, progress, token);

        return await _alignmentService.GenerateMapAsync(files.ToList(), centers.ToList(), target);
    }

    // =======================================================================
    // 3. ESECUZIONE (Pixel Warping Batch)
    // =======================================================================

    public async Task<List<string>> ExecuteWarpingAsync(
        IEnumerable<FitsFileReference> files,
        AlignmentMap map,
        IProgress<BatchProgressReport>? progress = null,
        CancellationToken token = default)
    {
        if (map == null || !map.IsValid) throw new InvalidOperationException("Mappa non valida.");

        var targetCenter = new Point2D(map.TargetSize.Width / 2.0, map.TargetSize.Height / 2.0);

        // Operazione batch che usa l'indice per accedere alla mappa dei centri
        Action<Mat, Mat, int> warpProcessor = (src, dst, index) =>
        {
            if (index >= map.Centers.Count || map.Centers[index] == null)
            {
                src.CopyTo(dst);
                return;
            }

            var sourcePoint = map.Centers[index]!.Value;
            if (map.Target == AlignmentTarget.Stars)
            {
                sourcePoint = new Point2D(sourcePoint.X - map.GlobalShift.X, sourcePoint.Y - map.GlobalShift.Y);
            }

            using var warped = _geometricEngine.WarpTranslation(src, sourcePoint, targetCenter, map.TargetSize);
            warped.CopyTo(dst); 
        };

        return await _batchService.ProcessFilesAsync(
            files.Select(f => f.FilePath), 
            "Aligned", 
            warpProcessor, 
            progress, 
            token);
    }

    // --- HELPERS ASTROMETRICI ---

    private async Task<Point2D?> FetchJplPointAsync(FitsFileReference file, string targetName)
    {
        var header = await _dataManager.GetHeaderOnlyAsync(file.FilePath);
        var date = _metadataService.GetObservationDate(header!);
        var loc = _metadataService.GetObservatoryLocation(header!);
        var wcs = _metadataService.ExtractWcs(header!);

        if (date == null || loc == null || !wcs.IsValid) return null;

        var ephem = await _jplService.GetEphemerisAsync(targetName, date.Value, loc);
        if (!ephem.HasValue) return null;

        var transform = new WcsTransformation(wcs, _metadataService.GetIntValue(header!, "NAXIS2"));
        return transform.WorldToPixel(ephem.Value.Ra, ephem.Value.Dec);
    }

    private async Task<Point2D?> FetchWcsPointAsync(FitsFileReference file, double ra, double dec)
    {
        var header = await _dataManager.GetHeaderOnlyAsync(file.FilePath);
        var wcs = _metadataService.ExtractWcs(header!);
        if (!wcs.IsValid) return null;

        var transform = new WcsTransformation(wcs, _metadataService.GetIntValue(header!, "NAXIS2"));
        return transform.WorldToPixel(ra, dec);
    }
}