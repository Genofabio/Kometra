using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KomaLab.Models.Fits;
using KomaLab.Models.Primitives;
using KomaLab.Models.Processing;
using KomaLab.Services.Fits;
using KomaLab.Services.Fits.Conversion;
using KomaLab.Services.Fits.Metadata;
using KomaLab.Services.Processing.Alignment.AlignmentStrategies;
using KomaLab.Services.Processing.Engines;
using OpenCvSharp;

namespace KomaLab.Services.Processing.Alignment;

public class AlignmentService : IAlignmentService
{
    private readonly IFitsDataManager _dataManager;
    private readonly IFitsMetadataService _metadataService;
    private readonly IFitsOpenCvConverter _converter;
    private readonly IImageAnalysisEngine _analysis;
    private readonly IGeometricEngine _geometricEngine;
    private readonly IImageEffectsEngine _effectsEngine;

    public AlignmentService(
        IFitsDataManager dataManager,
        IFitsMetadataService metadataService,
        IFitsOpenCvConverter converter,
        IImageAnalysisEngine analysis,
        IGeometricEngine geometricEngine,
        IImageEffectsEngine effectsEngine)
    {
        _dataManager = dataManager ?? throw new ArgumentNullException(nameof(dataManager));
        _metadataService = metadataService ?? throw new ArgumentNullException(nameof(metadataService));
        _converter = converter ?? throw new ArgumentNullException(nameof(converter));
        _analysis = analysis ?? throw new ArgumentNullException(nameof(analysis));
        _geometricEngine = geometricEngine ?? throw new ArgumentNullException(nameof(geometricEngine));
        _effectsEngine = effectsEngine ?? throw new ArgumentNullException(nameof(effectsEngine));
    }

    // =======================================================================
    // 1. CALCOLO CENTROIDI
    // =======================================================================

    public async Task<IEnumerable<Point2D?>> CalculateCentersAsync(
        AlignmentTarget target, 
        AlignmentMode mode,
        CenteringMethod method,
        List<FitsFileReference> files,
        IEnumerable<Point2D?> guesses,
        int searchRadius,
        IProgress<AlignmentProgressReport>? progress = null,
        CancellationToken token = default)
    {
        // Il Service delega la logica specifica alla strategia corretta (Manuale, Guidata, Auto)
        IAlignmentStrategy strategy = CreateStrategy(target, mode, method);
        return await strategy.CalculateAsync(files, guesses, searchRadius, progress, token);
    }

    // =======================================================================
    // 2. GENERAZIONE MAPPA GEOMETRICA
    // =======================================================================

    public async Task<AlignmentMap> GenerateMapAsync(
        List<FitsFileReference> files, 
        List<Point2D?> centers, 
        AlignmentTarget target)
    {
        Size2D targetSize;
        Point2D globalShift = new Point2D(0, 0);

        // Se allineiamo le stelle, dobbiamo trovare l'unione di tutti i campi (canvas grande)
        if (target == AlignmentTarget.Stars)
        {
            var result = await CalculateUnionGeometryAsync(files, centers);
            targetSize = result.Size;
            globalShift = result.Shift;
        }
        else
        {
            // Se allineiamo una cometa, vogliamo che la cometa sia al centro di un canvas
            // che contenga l'immagine più "estesa" dal suo centroide.
            targetSize = await CalculatePerfectCanvasSizeAsync(files, centers);
        }

        return new AlignmentMap
        {
            Centers = centers,
            TargetSize = targetSize,
            GlobalShift = globalShift,
            Target = target
        };
    }

    // =======================================================================
    // 3. PROCESSORE DI WARPING (Per Batch Service)
    // =======================================================================

    public Action<Mat, Mat, int> GetWarpingProcessor(AlignmentMap map)
    {
        // Ottimizzazione: Estraiamo i dati dalla mappa una sola volta
        var canvasCenter = new Point2D(map.TargetSize.Width / 2.0, map.TargetSize.Height / 2.0);
        var targetSize = map.TargetSize;
        var centers = map.Centers;
        var isStarAlignment = map.Target == AlignmentTarget.Stars;
        var globalShift = map.GlobalShift;

        // Restituiamo la Lambda che verrà eseguita in parallelo sui thread del BatchService
        return (src, dst, index) =>
        {
            if (index < 0 || index >= centers.Count || centers[index] == null)
            {
                src.CopyTo(dst);
                return;
            }

            Point2D sourcePoint = centers[index]!.Value;
            
            // Determiniamo la destinazione in base al target
            Point2D destinationPoint = isStarAlignment 
                ? globalShift 
                : canvasCenter;

            // Esecuzione della traslazione sub-pixel tramite l'Engine geometrico
            using var warped = _geometricEngine.WarpTranslation(
                src, 
                sourcePoint, 
                destinationPoint, 
                targetSize
            );
        
            warped.CopyTo(dst);
        };
    }

    // =======================================================================
    // 4. HELPERS GEOMETRICI
    // =======================================================================

    private async Task<(Size2D Size, Point2D Shift)> CalculateUnionGeometryAsync(
        List<FitsFileReference> files, List<Point2D?> centers)
    {
        double minX = 0, minY = 0, maxX = 0, maxY = 0;
        bool initialized = false;

        for (int i = 0; i < files.Count; i++)
        {
            if (i >= centers.Count || centers[i] == null) continue;
            
            var header = await _dataManager.GetHeaderOnlyAsync(files[i].FilePath);
            if (header == null) continue;

            double w = _metadataService.GetIntValue(header, "NAXIS1");
            double h = _metadataService.GetIntValue(header, "NAXIS2");
            var c = centers[i]!.Value;

            // Calcolo distanze relative dal centroide ai bordi
            double relL = -c.X;
            double relT = -c.Y;
            double relR = w - c.X;
            double relB = h - c.Y;

            if (!initialized) {
                minX = relL; maxX = relR; minY = relT; maxY = relB;
                initialized = true;
            } else {
                minX = Math.Min(minX, relL); maxX = Math.Max(maxX, relR);
                minY = Math.Min(minY, relT); maxY = Math.Max(maxY, relB);
            }
        }

        if (!initialized) return (new Size2D(1024, 1024), new Point2D(0, 0));

        return (
            new Size2D(Math.Ceiling(maxX - minX), Math.Ceiling(maxY - minY)), 
            new Point2D(-minX, -minY)
        );
    }

    private async Task<Size2D> CalculatePerfectCanvasSizeAsync(
        List<FitsFileReference> files, List<Point2D?> centers)
    {
        double maxRadiusX = 0, maxRadiusY = 0;
        
        // Usiamo un semaforo per non saturare la RAM caricando troppi FITS insieme
        using var semaphore = new SemaphoreSlim(Math.Clamp(Environment.ProcessorCount / 2, 1, 4));
        var tasks = new List<Task<(double Rx, double Ry)>>();

        for (int i = 0; i < files.Count; i++)
        {
            if (i >= centers.Count || centers[i] == null) continue;
            
            string path = files[i].FilePath;
            Point2D center = centers[i]!.Value;

            tasks.Add(Task.Run(async () =>
            {
                await semaphore.WaitAsync();
                try
                {
                    var data = await _dataManager.GetDataAsync(path);
                    using Mat mat = _converter.RawToMat(data.PixelData, _metadataService.GetDoubleValue(data.Header, "BSCALE", 1.0));
                    
                    Rect2D validBox = _analysis.FindValidDataBox(mat);
                    if (validBox.Width > 0)
                    {
                        double dx = Math.Max(center.X - validBox.X, (validBox.X + validBox.Width) - center.X);
                        double dy = Math.Max(center.Y - validBox.Y, (validBox.Y + validBox.Height) - center.Y);
                        return (dx, dy);
                    }
                    return (0.0, 0.0);
                }
                finally { semaphore.Release(); }
            }));
        }

        var results = await Task.WhenAll(tasks);
        foreach (var r in results)
        {
            maxRadiusX = Math.Max(maxRadiusX, r.Rx);
            maxRadiusY = Math.Max(maxRadiusY, r.Ry);
        }

        return new Size2D((int)Math.Ceiling(maxRadiusX * 2), (int)Math.Ceiling(maxRadiusY * 2));
    }

    private IAlignmentStrategy CreateStrategy(AlignmentTarget target, AlignmentMode mode, CenteringMethod method)
    {
        if (target == AlignmentTarget.Stars)
            return new StarAlignmentStrategy(_dataManager, _metadataService, _converter, _analysis);

        return mode switch
        {
            AlignmentMode.Manual => new ManualCometAlignmentStrategy(_dataManager, _metadataService, _converter, _analysis, method),
            AlignmentMode.Guided => new GuidedCometAlignmentStrategy(_dataManager, _metadataService, _converter, _analysis, _geometricEngine),
            AlignmentMode.Automatic => new AutomaticCometAlignmentStrategy(_dataManager, _metadataService, _converter, _analysis, _effectsEngine),
            _ => throw new NotSupportedException($"Modalità {mode} non supportata.")
        };
    }
}