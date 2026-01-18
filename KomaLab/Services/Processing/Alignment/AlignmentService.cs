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

    public AlignmentService(
        IFitsDataManager dataManager,
        IFitsMetadataService metadataService,
        IFitsOpenCvConverter converter,
        IImageAnalysisEngine analysis,
        IGeometricEngine geometricEngine)
    {
        _dataManager = dataManager ?? throw new ArgumentNullException(nameof(dataManager));
        _metadataService = metadataService ?? throw new ArgumentNullException(nameof(metadataService));
        _converter = converter ?? throw new ArgumentNullException(nameof(converter));
        _analysis = analysis ?? throw new ArgumentNullException(nameof(analysis));
        _geometricEngine = geometricEngine ?? throw new ArgumentNullException(nameof(geometricEngine));
    }

    // =======================================================================
    // 1. CALCOLO CENTROIDI (Strategie)
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
        // Istanziazione dinamica della strategia tramite factory interna
        IAlignmentStrategy strategy = CreateStrategy(target, mode, method);

        // Calcolo delegato: i guesses sono passati esternamente per mantenere puliti i modelli
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

        if (target == AlignmentTarget.Stars)
        {
            // Allineamento Siderale: Canvas "Unione" per preservare tutto il campo visibile
            var result = await CalculateUnionGeometryAsync(files, centers);
            targetSize = result.Size;
            globalShift = result.Shift;
        }
        else
        {
            // Allineamento Oggetto: Canvas centrato sul target con analisi dell'ingombro dati
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
    // 3. LOGICA GEOMETRICA AVANZATA (No Placeholders)
    // =======================================================================

    private async Task<(Size2D Size, Point2D Shift)> CalculateUnionGeometryAsync(
        List<FitsFileReference> files, List<Point2D?> centers)
    {
        double minLeft = double.MaxValue, minTop = double.MaxValue;
        double maxRight = double.MinValue, maxBottom = double.MinValue;
        bool hasData = false;

        for (int i = 0; i < files.Count; i++)
        {
            if (i >= centers.Count || centers[i] == null) continue;
            
            var header = await _dataManager.GetHeaderOnlyAsync(files[i].FilePath);
            if (header != null)
            {
                hasData = true;
                double w = _metadataService.GetIntValue(header, "NAXIS1");
                double h = _metadataService.GetIntValue(header, "NAXIS2");
                var c = centers[i]!.Value;

                // Calcolo spostamenti relativi rispetto al centroide
                minLeft = Math.Min(minLeft, -c.X);
                minTop = Math.Min(minTop, -c.Y);
                maxRight = Math.Max(maxRight, w - c.X);
                maxBottom = Math.Max(maxBottom, h - c.Y);
            }
        }

        if (!hasData) return (new Size2D(1024, 1024), new Point2D(0, 0));

        double totalW = Math.Ceiling(maxRight - minLeft);
        double totalH = Math.Ceiling(maxBottom - minTop);

        // Il GlobalShift corregge la posizione affinché l'intero stack rientri nel canvas positivo
        double shiftX = (totalW / 2.0) - (-minLeft);
        double shiftY = (totalH / 2.0) - (-minTop);

        return (new Size2D(totalW, totalH), new Point2D(shiftX, shiftY));
    }

    private async Task<Size2D> CalculatePerfectCanvasSizeAsync(
        List<FitsFileReference> files, List<Point2D?> centers)
    {
        double maxRadiusX = 0, maxRadiusY = 0;
        // Parallelismo limitato per proteggere la RAM durante l'analisi pixel
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
                    using Mat mat = _converter.RawToMat(data.PixelData, 
                        _metadataService.GetDoubleValue(data.Header, "BSCALE", 1.0));
                    
                    // Identificazione dei bordi reali dei dati astronomici
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

    // =======================================================================
    // 4. FACTORY INTERNA (Cablaggio Strategie)
    // =======================================================================

    private IAlignmentStrategy CreateStrategy(AlignmentTarget target, AlignmentMode mode, CenteringMethod method)
    {
        // Nota: Le strategie ricevono i servizi necessari tramite costruttore
        if (target == AlignmentTarget.Stars)
        {
            return new StarAlignmentStrategy(_dataManager, _metadataService, _converter, _analysis);
        }

        return mode switch
        {
            AlignmentMode.Manual => new ManualCometAlignmentStrategy(_dataManager, _metadataService, _converter, _analysis, method),
            AlignmentMode.Guided => new GuidedCometAlignmentStrategy(_dataManager, _metadataService, _converter, _analysis, _geometricEngine),
            AlignmentMode.Automatic => new AutomaticCometAlignmentStrategy(_dataManager, _metadataService, _converter, _analysis),
            _ => throw new NotSupportedException($"Modalità {mode} non supportata per {target}.")
        };
    }
}