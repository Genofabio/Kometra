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
using OpenCvSharp;

namespace KomaLab.Services.Processing.Coordinators;

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
        // Priorità all'header in RAM per rispettare le modifiche dell'utente durante la sessione
        var header = file.ModifiedHeader ?? await _dataManager.GetHeaderOnlyAsync(file.FilePath);
        if (header == null) throw new InvalidOperationException("Header FITS non leggibile.");

        string obj = _metadataService.GetStringValue(header, "OBJECT").Replace("'", "").Trim();
        var wcs = _metadataService.ExtractWcs(header);

        return new AlignmentTargetMetadata(
            ObjectName: string.IsNullOrWhiteSpace(obj) ? "Unknown" : obj,
            ObservationDate: _metadataService.GetObservationDate(header), // Data ISO-8601 precisa
            Location: _metadataService.GetObservatoryLocation(header),
            HasWcs: wcs.IsValid,
            ImageWidth: _metadataService.GetIntValue(header, "NAXIS1"),
            ImageHeight: _metadataService.GetIntValue(header, "NAXIS2"),
            // [SCIENTIFIC FIX] Segnalazione Tracking Non-Siderale (Stelle Strisciate)
            // Nota: Assicurati di aggiungere 'bool IsTracked' al record AlignmentTargetMetadata
            IsTracked: _metadataService.IsMovingTarget(header) 
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
            var firstFile = fileList[0];
            var refHeader = firstFile.ModifiedHeader ?? await _dataManager.GetHeaderOnlyAsync(firstFile.FilePath);
            var refWcs = _metadataService.ExtractWcs(refHeader!);
            
            foreach (var f in fileList)
                results.Add(await FetchWcsPointAsync(f, refWcs.RefRaDeg, refWcs.RefDecDeg));
        }
        else if (target == AlignmentTarget.Comet && !string.IsNullOrEmpty(targetName))
        {
            foreach (var f in fileList)
                results.Add(await FetchJplPointAsync(f, targetName));
        }
        else
        {
            results.AddRange(Enumerable.Repeat<Point2D?>(null, fileList.Count));
        }

        return results;
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

        if (guessList.All(g => g == null) && !string.IsNullOrEmpty(jplTargetName))
        {
            guessList = await DiscoverStartingPointsAsync(fileList, target, jplTargetName);
        }

        // Delega allo specialista (AlignmentService) il calcolo dei centroidi reali
        var centers = await _alignmentService.CalculateCentersAsync(
            target, mode, method, fileList, guessList, searchRadius, progress, token);

        // Genera la mappa geometrica (Canvas size e spostamenti relativi)
        return await _alignmentService.GenerateMapAsync(fileList, centers.ToList(), target);
    }

    public async Task<List<string>> ExecuteWarpingAsync(
        IEnumerable<FitsFileReference> files,
        AlignmentMap map,
        AlignmentMode mode,
        int searchRadius,
        string? jplName, 
        IProgress<BatchProgressReport>? progress = null,
        CancellationToken token = default)
    {
        if (map == null || !map.IsValid) throw new InvalidOperationException("Mappa non valida.");

        // 1. Prepariamo la stringa "Statica"
        string refInfo = "WCS";
        if (map.Target == AlignmentTarget.Comet)
        {
            refInfo = !string.IsNullOrEmpty(jplName) ? $"JPL ({jplName})" : "Manual";
        }

        // 2. Definiamo la Strategia di Logging (HISTORY)
        Func<double, double, string> logStrategy = (dx, dy) => 
        {
            return FormattableString.Invariant(
                $"KomaLab Align: Mode={mode}, Rad={searchRadius}, Ref={refInfo}, dX={dx:F2}, dY={dy:F2}"
            );
        };

        // 3. Otteniamo il processore di warping configurato
        var warpProcessor = _alignmentService.GetWarpingProcessor(map, logStrategy);

        // 4. Eseguiamo il batch
        return await _batchService.ProcessFilesAsync(files, "Aligned", warpProcessor, progress, token);
    }

    #region Helpers Astrometrici Privati

    private async Task<Point2D?> FetchJplPointAsync(FitsFileReference file, string targetName)
    {
        var header = file.ModifiedHeader ?? await _dataManager.GetHeaderOnlyAsync(file.FilePath);
        
        // [SCIENTIFIC FIX] Calcolo del tempo per orbite precise
        // Per oggetti veloci (Comete/NEO), la posizione deve essere riferita al
        // PUNTO MEDIO dell'esposizione (DATE-OBS + EXPTIME/2), non all'inizio.
        var date = _metadataService.GetObservationMidPoint(header!) 
                   ?? _metadataService.GetObservationDate(header!);
                   
        var loc = _metadataService.GetObservatoryLocation(header!);
        var wcs = _metadataService.ExtractWcs(header!);

        if (date == null || loc == null || !wcs.IsValid) return null;

        // Interrogazione JPL Horizons con il tempo corretto
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