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
        _alignmentService = alignmentService;
        _batchService = batchService;
        _dataManager = dataManager;
        _metadataService = metadataService;
        _jplService = jplService;
    }

    public async Task<AlignmentTargetMetadata> GetFileMetadataAsync(FitsFileReference file)
    {
        var header = await _dataManager.GetHeaderOnlyAsync(file.FilePath);
        if (header == null) throw new InvalidOperationException("Header FITS non leggibile.");

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

        // Logica di instradamento: WCS per stelle, JPL per comete
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
        else
        {
            // Fallback: lista vuota se non ci sono dati astrometrici
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

        // --- LOGICA DI COORDINAMENTO INTELLIGENTE ---
        // Se la UI non ha fornito coordinate (perché eravamo in Automatico)
        // ma l'utente ha abilitato JPL (abbiamo un jplTargetName), le recuperiamo ora.
        if (guessList.All(g => g == null) && !string.IsNullOrEmpty(jplTargetName))
        {
            guessList = await DiscoverStartingPointsAsync(fileList, target, jplTargetName);
        }

        // Delega il calcolo matematico dei centroidi al Service
        var centers = await _alignmentService.CalculateCentersAsync(
            target, mode, method, fileList, guessList, searchRadius, progress, token);

        // Delega la creazione della mappa geometrica (canvas, shift globali) al Service
        return await _alignmentService.GenerateMapAsync(fileList, centers.ToList(), target);
    }

    public async Task<List<string>> ExecuteWarpingAsync(
        IEnumerable<FitsFileReference> files,
        AlignmentMap map,
        IProgress<BatchProgressReport>? progress = null,
        CancellationToken token = default)
    {
        if (map == null || !map.IsValid) throw new InvalidOperationException("Mappa di allineamento non valida.");

        // Il Coordinator chiede al Service IL COME (la funzione di warping)
        // ma chiede al BatchService IL QUANDO (l'esecuzione parallela sui file)
        Action<Mat, Mat, int> warpProcessor = _alignmentService.GetWarpingProcessor(map);

        return await _batchService.ProcessFilesAsync(
            files.Select(f => f.FilePath), 
            "Aligned", 
            warpProcessor, 
            progress, 
            token);
    }

    #region Helpers Astrometrici Privati

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

    #endregion
}