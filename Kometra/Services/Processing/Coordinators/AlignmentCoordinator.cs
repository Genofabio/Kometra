using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kometra.Models.Fits;
using Kometra.Models.Primitives;
using Kometra.Models.Processing;
using Kometra.Models.Processing.Alignment;
using Kometra.Models.Processing.Batch;
using Kometra.Services.Astrometry;
using Kometra.Services.Fits;
using Kometra.Services.Fits.Metadata;
using Kometra.Services.Processing.Alignment;
using Kometra.Services.Processing.Batch;

namespace Kometra.Services.Processing.Coordinators;

public class AlignmentCoordinator : IAlignmentCoordinator
{
    private readonly IAlignmentService _alignmentService;
    private readonly IBatchProcessingService _batchService;
    private readonly IFitsDataManager _dataManager;
    private readonly IFitsMetadataService _metadataService;
    private readonly IJplHorizonsService _jplService;

    public AlignmentCoordinator(
        IAlignmentService alignmentService,
        IBatchProcessingService batchService,
        IFitsDataManager dataManager,
        IFitsMetadataService metadataService,
        IJplHorizonsService jplService)
    {
        _alignmentService = alignmentService ?? throw new ArgumentNullException(nameof(alignmentService));
        _batchService = batchService ?? throw new ArgumentNullException(nameof(batchService));
        _dataManager = dataManager ?? throw new ArgumentNullException(nameof(dataManager));
        _metadataService = metadataService ?? throw new ArgumentNullException(nameof(metadataService));
        _jplService = jplService ?? throw new ArgumentNullException(nameof(jplService));
    }

    public async Task<AlignmentTargetMetadata> GetFileMetadataAsync(FitsFileReference file)
    {
        var header = file.ModifiedHeader ?? await _dataManager.GetHeaderOnlyAsync(file.FilePath);
        if (header == null) throw new InvalidOperationException("Header FITS non leggibile.");

        string obj = _metadataService.GetStringValue(header, "OBJECT").Replace("'", "").Trim();
        var wcs = _metadataService.ExtractWcs(header);

        return new AlignmentTargetMetadata(
            ObjectName: string.IsNullOrWhiteSpace(obj) ? "Unknown" : obj,
            ObservationDate: _metadataService.GetObservationDate(header),
            Location: _metadataService.GetObservatoryLocation(header),
            HasWcs: wcs.IsValid,
            ImageWidth: _metadataService.GetIntValue(header, "NAXIS1"),
            ImageHeight: _metadataService.GetIntValue(header, "NAXIS2"),
            IsTracked: _metadataService.IsMovingTarget(header) 
        );
    }

    public async Task<List<Point2D?>> DiscoverStartingPointsAsync(
        IEnumerable<FitsFileReference> files,
        AlignmentTarget target,
        string? targetName)
    {
        var fileList = files.ToList();
        
        if (target == AlignmentTarget.Comet && string.IsNullOrEmpty(targetName))
            return Enumerable.Repeat<Point2D?>(null, fileList.Count).ToList();

        // Limitiamo a 2 richieste simultanee verso JPL/Disco per stabilità
        using var semaphore = new SemaphoreSlim(2);

        var tasks = fileList.Select(async file =>
        {
            if (target == AlignmentTarget.Stars) return (Point2D?)null;

            await semaphore.WaitAsync();
            try
            {
                if (target == AlignmentTarget.Comet)
                {
                    try 
                    {
                        return await FetchJplPointAsync(file, targetName!);
                    }
                    catch 
                    {
                        return null; 
                    }
                }
                return null;
            }
            finally
            {
                semaphore.Release();
            }
        }).ToList();

        if (target == AlignmentTarget.Stars)
        {
            var firstFile = fileList[0];
            var refHeader = firstFile.ModifiedHeader ?? await _dataManager.GetHeaderOnlyAsync(firstFile.FilePath);
            var refWcs = _metadataService.ExtractWcs(refHeader!);
            
            using var starSemaphore = new SemaphoreSlim(8); 

            var starTasks = fileList.Select(async f => 
            {
                await starSemaphore.WaitAsync();
                try 
                {
                    return await FetchWcsPointAsync(f, refWcs.RefRaDeg, refWcs.RefDecDeg);
                }
                finally { starSemaphore.Release(); }
            });

            return (await Task.WhenAll(starTasks)).ToList();
        }

        var results = await Task.WhenAll(tasks);
        return results.ToList();
    }

    public async Task<AlignmentMap> AnalyzeSequenceAsync(
        IEnumerable<FitsFileReference> files,
        IEnumerable<Point2D?> guesses,
        AlignmentTarget target,
        AlignmentMode mode,
        CenteringMethod method,
        int searchRadius,
        string? jplTargetName = null,
        IProgress<AlignmentProgressReport>? progress = null,
        CancellationToken token = default)
    {
        var fileList = files.ToList();
        var guessList = guesses.ToList();

        // 1. Discovery automatica se mancano dati manuali
        if (guessList.Any(g => g == null) && !string.IsNullOrEmpty(jplTargetName))
        {
            var discoveredPoints = await DiscoverStartingPointsAsync(fileList, target, jplTargetName);

            for (int i = 0; i < guessList.Count; i++)
            {
                if (guessList[i] == null && i < discoveredPoints.Count)
                {
                    guessList[i] = discoveredPoints[i];
                }
            }
        }

        // 2. Calcolo Scientifico dei Centri
        var centers = await _alignmentService.CalculateCentersAsync(
            target, mode, method, fileList, guessList, searchRadius, progress, token);

        // 3. Generazione Mappa di Analisi
        // NOTA: Passiamo cropToCommonArea: false perché in fase di analisi (preview) 
        // vogliamo solitamente vedere tutto il contesto o non ci importa del crop finale.
        // Il crop vero e proprio viene deciso in ExecuteWarpingAsync.
        return await _alignmentService.GenerateMapAsync(
            fileList, 
            centers.ToList(), 
            target, 
            cropToCommonArea: false); 
    }

    public async Task<List<string>> ExecuteWarpingAsync(
        IEnumerable<FitsFileReference> files,
        AlignmentMap map,
        AlignmentMode mode,
        int searchRadius,
        string? jplName, 
        bool cropToCommonArea, // <--- PARAMETRO NUOVO
        IProgress<BatchProgressReport>? progress = null,
        CancellationToken token = default)
    {
        if (map == null || !map.IsValid) throw new InvalidOperationException("Mappa non valida.");

        var fileList = files.ToList();

        // 1. RIGENERAZIONE MAPPA GEOMETRICA
        // La mappa in ingresso contiene i centri corretti, ma potrebbe avere una geometria "Union" di default.
        // Qui forziamo il ricalcolo della geometria (Size2D e Shift) in base alla scelta dell'utente (checkbox).
        var finalMap = await _alignmentService.GenerateMapAsync(
            fileList, 
            map.Centers.ToList(), 
            map.Target, 
            cropToCommonArea); // Usa la scelta UI

        // 2. Preparazione Logging
        string refInfo = "WCS";
        if (finalMap.Target == AlignmentTarget.Comet)
        {
            refInfo = !string.IsNullOrEmpty(jplName) ? $"JPL ({jplName})" : "Manual";
        }

        Func<double, double, string> logStrategy = (dx, dy) => 
        {
            return FormattableString.Invariant(
                $"Kometra Align: Mode={mode}, Rad={searchRadius}, Ref={refInfo}, dX={dx:F2}, dY={dy:F2}, Cropped={cropToCommonArea}"
            );
        };

        // 3. Ottenimento Processore e Esecuzione Batch
        var warpProcessor = _alignmentService.GetWarpingProcessor(finalMap, logStrategy);

        return await _batchService.ProcessFilesAsync(fileList, "Aligned", warpProcessor, progress, token);
    }

    #region Helpers Astrometrici Privati

    private async Task<Point2D?> FetchJplPointAsync(FitsFileReference file, string targetName)
    {
        var header = file.ModifiedHeader ?? await _dataManager.GetHeaderOnlyAsync(file.FilePath);
        
        var date = _metadataService.GetObservationMidPoint(header!) 
                   ?? _metadataService.GetObservationDate(header!);
                   
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
        var header = file.ModifiedHeader ?? await _dataManager.GetHeaderOnlyAsync(file.FilePath);
        var wcs = _metadataService.ExtractWcs(header!);
        if (!wcs.IsValid) return null;

        var transform = new WcsTransformation(wcs, _metadataService.GetIntValue(header!, "NAXIS2"));
        return transform.WorldToPixel(ra, dec);
    }

    #endregion
}