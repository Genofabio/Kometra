using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KomaLab.Models.Fits;
using KomaLab.Models.Primitives;
using KomaLab.Models.Processing;
using KomaLab.Services.Fits;
using KomaLab.Services.Processing.AlignmentStrategies;
using OpenCvSharp;

namespace KomaLab.Services.Processing;

// ---------------------------------------------------------------------------
// FILE: AlignmentService.cs
// RUOLO: Orchestratore Allineamento (Engine Principale)
// VERSIONE: Aggiornata per Architettura No-FitsImageData
// ---------------------------------------------------------------------------

public class AlignmentService : IAlignmentService
{
    private readonly IFitsIoService _ioService;
    private readonly IFitsOpenCvConverter _converter;
    private readonly IFitsMetadataService _metadataService;
    private readonly IImageAnalysisService _analysis;
    private readonly IImageOperationService _operations;

    public AlignmentService(
        IFitsIoService ioService,
        IFitsOpenCvConverter converter,
        IFitsMetadataService metadataService,
        IImageAnalysisService analysis,
        IImageOperationService operations)
    {
        _ioService = ioService ?? throw new ArgumentNullException(nameof(ioService));
        _converter = converter ?? throw new ArgumentNullException(nameof(converter));
        _metadataService = metadataService ?? throw new ArgumentNullException(nameof(metadataService));
        _analysis = analysis ?? throw new ArgumentNullException(nameof(analysis));
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
    }

    // =======================================================================
    // 1. FASE DI CALCOLO (Delegata alle Strategie)
    // =======================================================================

    public async Task<IEnumerable<Point2D?>> CalculateCentersAsync(
        AlignmentTarget target, 
        AlignmentMode mode,
        CenteringMethod method,
        List<string> sourcePaths,
        IEnumerable<Point2D?> currentCoordinates,
        int searchRadius,
        IProgress<(int Index, Point2D? Center)>? progress = null)
    {
        IAlignmentStrategy strategy = CreateStrategy(target, mode, method);

        var results = await strategy.CalculateAsync(
            sourcePaths, 
            currentCoordinates.ToList(), 
            searchRadius, 
            progress);

        return results;
    }

    // Nota: Le strategie dovranno essere aggiornate internamente per non usare FitsImageData,
    // ma l'interfaccia pubblica IAlignmentStrategy lavora già con List<string> e Point2D,
    // quindi questo codice non cambia.
    private IAlignmentStrategy CreateStrategy(AlignmentTarget target, AlignmentMode mode, CenteringMethod method)
    {
        if (target == AlignmentTarget.Stars)
        {
            return new StarAlignmentStrategy(_ioService, _converter, _analysis);
        }
        
        if (target == AlignmentTarget.Comet)
        {
            switch (mode)
            {
                case AlignmentMode.Guided:
                    return new GuidedCometAlignmentStrategy(_ioService, _converter, _operations);
                
                case AlignmentMode.Automatic:
                    return new AutomaticCometAlignmentStrategy(_ioService, _converter, _analysis);

                case AlignmentMode.Manual:
                    return new ManualCometAlignmentStrategy(_ioService, _converter, _analysis, method);
                
                default:
                    throw new ArgumentOutOfRangeException(nameof(mode), mode, "Modalità Cometa non supportata.");
            }
        }

        throw new ArgumentOutOfRangeException(nameof(target), target, "Target di allineamento non supportato.");
    }

    public bool CanCalculate(AlignmentTarget target, AlignmentMode mode, IEnumerable<Point2D?> currentCoordinates, int totalCount)
    {
        if (totalCount == 0) return false;
        var list = currentCoordinates.ToList();
        
        if (target == AlignmentTarget.Stars) return true;

        switch (mode)
        {
            case AlignmentMode.Automatic: 
                return true; 
            
            case AlignmentMode.Guided:
                if (totalCount <= 1) return list.Count > 0 && list[0].HasValue;
                bool hasFirst = list.Count > 0 && list[0].HasValue;
                bool hasLast = list.Count >= totalCount && list[totalCount - 1].HasValue;
                return hasFirst && hasLast;
            
            case AlignmentMode.Manual: 
                return list.Count == totalCount && list.All(e => e.HasValue);
            
            default: return false;
        }
    }

    // =======================================================================
    // 2. FASE DI APPLICAZIONE (Centering & Saving)
    // =======================================================================

    public async Task<List<string>> ApplyCenteringAndSaveAsync(
        List<string> sourcePaths,
        List<Point2D?> centers,
        string tempFolderPath,
        AlignmentTarget target) 
    {
        Size2D finalSize;
        Point2D offsetCorrection = new Point2D(0, 0);

        // A. Calcolo Dimensioni Canvas
        if (target == AlignmentTarget.Stars)
        {
            var result = await CalculateUnionBoundingBoxAsync(sourcePaths, centers);
            finalSize = result.Size;
            offsetCorrection = result.ShiftCorrection;
        }
        else
        {
            finalSize = await CalculatePerfectCanvasSizeAsync(sourcePaths, centers);
        }

        // B. Configurazione Concorrenza
        long firstFileSize = 0;
        try { if (sourcePaths.Count > 0) firstFileSize = new FileInfo(sourcePaths[0]).Length; } catch { }
        
        // Se file > 100MB, 1 alla volta. Altrimenti parallelizziamo.
        int maxConcurrency = (firstFileSize > 100 * 1024 * 1024) ? 1 : Math.Clamp(Environment.ProcessorCount / 2, 2, 4);

        using var semaphore = new SemaphoreSlim(maxConcurrency);
        var tasks = new List<Task<string?>>();

        if (!Directory.Exists(tempFolderPath))
            Directory.CreateDirectory(tempFolderPath);

        // C. Loop di Processamento
        for (int i = 0; i < sourcePaths.Count; i++)
        {
            string path = sourcePaths[i];
            var center = centers[i];
            int index = i;

            if (center == null) continue;

            Point2D adjustedCenter = new Point2D(center.Value.X + offsetCorrection.X, center.Value.Y + offsetCorrection.Y);

            tasks.Add(Task.Run(async () =>
            {
                await semaphore.WaitAsync();
                try
                {
                    return await AttemptProcessAndSaveWithRetryAsync(
                        index, path, adjustedCenter, finalSize, tempFolderPath
                    );
                }
                finally { semaphore.Release(); }
            }));
        }

        var results = await Task.WhenAll(tasks);
        return results.Where(p => p != null).Cast<string>().ToList();
    }

    // --- METODO CRITICO AGGIORNATO ---
    private async Task<string?> AttemptProcessAndSaveWithRetryAsync(
        int index, string path, Point2D? center, Size2D targetSize, string tempFolderPath)
    {
        if (center == null) return null;
        int attempts = 0;
        
        while (true)
        {
            attempts++;
            try
            {
                // 1. CARICAMENTO (I/O)
                var oldHeader = await _ioService.ReadHeaderAsync(path);
                var rawPixels = await _ioService.ReadPixelDataAsync(path);
                
                if (oldHeader == null || rawPixels == null) return null;

                if (attempts > 1) { GC.Collect(); await Task.Delay(100); }

                // 2. PROCESSING (CPU)
                // Ora Task.Run restituisce un tipo semplice: Array. Nessuna confusione per il compilatore.
                Array newPixels = await Task.Run(() =>
                {
                    double bScale = oldHeader.GetValue<double>("BSCALE") ?? 1.0;
                    double bZero = oldHeader.GetValue<double>("BZERO") ?? 0.0;

                    using Mat originalMat = _converter.RawToMat(rawPixels, bScale, bZero);
                    using Mat centeredMat = _operations.GetSubPixelCenteredCanvas(originalMat, center.Value, targetSize);
                    
                    // Il convertitore ora ritorna SOLO l'array dei pixel
                    return _converter.MatToRaw(centeredMat, FitsBitDepth.Double);
                });

                // 3. COSTRUZIONE HEADER (CPU - Leggera)
                // Usiamo il servizio di metadata per creare l'header corretto
                var newHeader = _metadataService.CreateHeaderFromTemplate(oldHeader, newPixels, FitsBitDepth.Double);

                // 4. SALVATAGGIO (I/O)
                string fileName = $"Aligned_{index}_{Guid.NewGuid()}.fits";
                string fullPath = Path.Combine(tempFolderPath, fileName);
                
                await _ioService.WriteFileAsync(fullPath, newPixels, newHeader);

                return fullPath;
            }
            catch (Exception ex)
            {
                // Gestione errori invariata
                if (attempts >= 3) return null;
                await Task.Delay(300 * attempts);
            }
        }
    }
    
    // =======================================================================
    // 3. CALCOLI GEOMETRICI DI SUPPORTO
    // =======================================================================

    private async Task<(Size2D Size, Point2D ShiftCorrection)> CalculateUnionBoundingBoxAsync(
        List<string> paths, 
        List<Point2D?> centers)
    {
        double minLeft = double.MaxValue;
        double minTop = double.MaxValue;
        double maxRight = double.MinValue;
        double maxBottom = double.MinValue;

        bool hasData = false;

        for (int i = 0; i < paths.Count; i++)
        {
            if (i >= centers.Count || centers[i] == null) continue;
            Point2D c = centers[i]!.Value;

            // HeaderOnly è perfetto qui, non serve leggere i pixel
            var header = await _ioService.ReadHeaderAsync(paths[i]);
            if (header != null)
            {
                hasData = true;
                double w = header.GetIntValue("NAXIS1");
                double h = header.GetIntValue("NAXIS2");

                double relLeft = -c.X;
                double relTop = -c.Y;
                double relRight = w - c.X;
                double relBottom = h - c.Y;

                if (relLeft < minLeft) minLeft = relLeft;
                if (relTop < minTop) minTop = relTop;
                if (relRight > maxRight) maxRight = relRight;
                if (relBottom > maxBottom) maxBottom = relBottom;
            }
        }

        if (!hasData) return (new Size2D(100, 100), new Point2D(0, 0));

        double totalW = Math.Ceiling(maxRight - minLeft);
        double totalH = Math.Ceiling(maxBottom - minTop);
        
        double idealCenterX = -minLeft; 
        double idealCenterY = -minTop;  
        double canvasCenterX = totalW / 2.0;
        double canvasCenterY = totalH / 2.0;

        double shiftX = canvasCenterX - idealCenterX;
        double shiftY = canvasCenterY - idealCenterY;

        return (new Size2D(totalW, totalH), new Point2D(shiftX, shiftY));
    }
    
    private async Task<Size2D> CalculatePerfectCanvasSizeAsync(List<string> paths, List<Point2D?> centers)
    {
        double maxRadiusX = 0;
        double maxRadiusY = 0;

        long firstFileSize = 0;
        try { if (paths.Count > 0) firstFileSize = new FileInfo(paths[0]).Length; } catch { }
        int maxConcurrency = (firstFileSize > 50 * 1024 * 1024) ? 2 : 4; 

        using var semaphore = new SemaphoreSlim(maxConcurrency);
        var tasks = new List<Task<(double Rx, double Ry)>>();

        for (int i = 0; i < paths.Count; i++)
        {
            if (i >= centers.Count || centers[i] == null) continue;
            
            int index = i;
            Point2D center = centers[i]!.Value;
            string path = paths[i];

            tasks.Add(Task.Run(async () =>
            {
                await semaphore.WaitAsync();
                try
                {
                    // MODIFICA: Caricamento pixel per calcolare la box dei dati validi
                    var header = await _ioService.ReadHeaderAsync(path); // Serve per BSCALE
                    var rawPixels = await _ioService.ReadPixelDataAsync(path);
                    
                    if (header == null || rawPixels == null) return (0, 0);

                    // Estrazione parametri di scala
                    double bScale = header.GetValue<double>("BSCALE") ?? 1.0;
                    double bZero = header.GetValue<double>("BZERO") ?? 0.0;

                    using Mat mat = _converter.RawToMat(rawPixels, bScale, bZero);
                    
                    // Analisi Dati Solidi
                    Rect2D validBox = _analysis.FindValidDataBox(mat);

                    if (validBox.Width > 0 && validBox.Height > 0)
                    {
                        double distLeft = center.X - validBox.X;
                        double distRight = (validBox.X + validBox.Width) - center.X;
                        double distTop = center.Y - validBox.Y;
                        double distBottom = (validBox.Y + validBox.Height) - center.Y;

                        double myRadiusX = Math.Max(distLeft, distRight);
                        double myRadiusY = Math.Max(distTop, distBottom);
                        return (myRadiusX, myRadiusY);
                    }
                    return (0.0, 0.0);
                }
                finally { semaphore.Release(); }
            }));
        }

        var results = await Task.WhenAll(tasks);

        foreach (var res in results)
        {
            if (res.Rx > maxRadiusX) maxRadiusX = res.Rx;
            if (res.Ry > maxRadiusY) maxRadiusY = res.Ry;
        }

        int finalW = (int)Math.Ceiling(maxRadiusX * 2);
        int finalH = (int)Math.Ceiling(maxRadiusY * 2);
        
        return (finalW > 0 && finalH > 0) ? new Size2D(finalW, finalH) : new Size2D(1000, 1000);
    }
}