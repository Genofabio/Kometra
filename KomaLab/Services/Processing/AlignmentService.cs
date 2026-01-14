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
// RUOLO: Orchestratore Allineamento
// DESCRIZIONE:
// Gestisce l'intero pipeline di allineamento delle immagini astronomiche.
// 1. Fase di Calcolo: Delega a strategie specifiche (Stelle, Cometa Blind/Guided)
//    il calcolo dei centroidi di ogni frame.
// 2. Fase di Applicazione: Applica lo shift (e rotazione se necessario) ai pixel,
//    calcola il canvas ottimale (Unione/Intersezione) e salva i frame intermedi.
// ---------------------------------------------------------------------------

public class AlignmentService : IAlignmentService
{
    private readonly IFitsIoService _ioService;      // Sostituisce il vecchio FitsService
    private readonly IFitsImageDataConverter _converter;
    private readonly IFitsMetadataService _metadataService; // Necessario per clonare gli header
    private readonly IImageAnalysisService _analysis;
    private readonly IImageOperationService _operations;

    public AlignmentService(
        IFitsIoService ioService,
        IFitsImageDataConverter converter,
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
        // Factory Method interno per creare la Strategy corretta
        IAlignmentStrategy strategy = CreateStrategy(target, mode, method);

        // Esecuzione incapsulata del calcolo
        var results = await strategy.CalculateAsync(
            sourcePaths, 
            currentCoordinates.ToList(), 
            searchRadius, 
            progress);

        return results;
    }

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
            // Per le stelle: UNIONE (allargare il campo per includere tutte le stelle visibili)
            var result = await CalculateUnionBoundingBoxAsync(sourcePaths, centers);
            finalSize = result.Size;
            offsetCorrection = result.ShiftCorrection;
        }
        else
        {
            // Per le comete: INTERSEZIONE OTTIMIZZATA
            // (evitiamo di caricare tutti i file se possibile, vedi metodo sotto)
            finalSize = await CalculatePerfectCanvasSizeAsync(sourcePaths, centers);
        }

        // B. Configurazione Concorrenza (Throttling)
        long firstFileSize = 0;
        try { if (sourcePaths.Count > 0) firstFileSize = new FileInfo(sourcePaths[0]).Length; } catch { }
        
        // Se file > 100MB, 1 alla volta. Altrimenti parallelizziamo moderatamente.
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

            // Applica offset globale per l'Unione dei campi (per le comete solitamente è 0,0)
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

    private async Task<string?> AttemptProcessAndSaveWithRetryAsync(
        int index, string path, Point2D? center, Size2D targetSize, string tempFolderPath)
    {
        if (center == null) return null;
        int attempts = 0;
        
        // Manteniamo il retry qui NON per problemi di file lock (già risolti dal provider),
        // ma per problemi di allocazione memoria (OutOfMemory) durante il processing pesante.
        while (true)
        {
            attempts++;
            try
            {
                // 1. Carica dati originali
                FitsImageData? inputData = await _ioService.LoadAsync(path);
                if (inputData == null) return null;

                if (attempts > 1) { GC.Collect(); await Task.Delay(100); }

                // 2. Processing (Shift)
                FitsImageData outputData = await Task.Run(() =>
                {
                    using Mat originalMat = _converter.RawToMat(inputData);
                    
                    // Applica lo shift sub-pixel e ritaglia/estende al nuovo canvas
                    using Mat centeredMat = _operations.GetSubPixelCenteredCanvas(originalMat, center.Value, targetSize);
                    
                    // Riconverte in oggetto FITS
                    var resultData = _converter.MatToFitsData(centeredMat);
                    
                    // 3. Trasferisce i metadati originali nel nuovo risultato
                    // Importante: FitsMetadataService gestirà la pulizia delle chiavi geometriche obsolete
                    _metadataService.TransferMetadata(inputData.FitsHeader, resultData.FitsHeader);
                    
                    return resultData;
                });

                string fileName = $"Aligned_{index}_{Guid.NewGuid()}.fits";
                string fullPath = Path.Combine(tempFolderPath, fileName);
                
                // 4. Salva risultato
                await _ioService.SaveAsync(outputData, fullPath);

                return fullPath;
            }
            catch (Exception)
            {
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

            // Leggiamo solo l'header per velocità (fondamentale!)
            var header = await _ioService.ReadHeaderOnlyAsync(paths[i]);
            if (header != null)
            {
                hasData = true;
                double w = header.GetIntValue("NAXIS1");
                double h = header.GetIntValue("NAXIS2");

                // Calcolo bounding box relativa al centro di allineamento
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
        
        // Offset per centrare il tutto nel nuovo canvas
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

        // Configurazione Concorrenza per l'analisi (qui dobbiamo caricare i file!)
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
                    // DOBBIAMO caricare l'immagine per sapere dove sono i dati validi
                    // (Esattamente come il vecchio codice)
                    var data = await _ioService.LoadAsync(path);
                    if (data == null) return (0, 0);

                    using Mat mat = _converter.RawToMat(data);
                    
                    // Qui usiamo la versione FIXATA di FindValidDataBox (che include i neri ma esclude i NaN)
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

        // Calcolo finale identico al vecchio codice
        int finalW = (int)Math.Ceiling(maxRadiusX * 2);
        int finalH = (int)Math.Ceiling(maxRadiusY * 2);
        
        return (finalW > 0 && finalH > 0) ? new Size2D(finalW, finalH) : new Size2D(1000, 1000);
    }
}