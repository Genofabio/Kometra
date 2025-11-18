using KomaLab.ViewModels; 
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KomaLab.Models;
using OpenCvSharp;
using Point = Avalonia.Point;

namespace KomaLab.Services;

/// <summary>
/// Implementazione del servizio di logica di business dell'allineamento.
/// Orchestra il processo chiamando altri servizi (es. ImageProcessing).
/// </summary>
public class AlignmentService : IAlignmentService
{
    private readonly IImageProcessingService _processingService;
    
    public AlignmentService(IImageProcessingService processingService)
    {
        _processingService = processingService;
    }

    /// <summary>
    /// Esegue il calcolo dei centri per un set di immagini in base alla modalità scelta.
    /// </summary>
    public async Task<IEnumerable<Point?>> CalculateCentersAsync(
    AlignmentMode mode, 
    CenteringMethod method, 
    List<FitsImageData?> sourceData, 
    IEnumerable<Point?> currentCoordinates, 
    int searchRadius)
{
    // Uscita rapida
    if (searchRadius <= 0 && (mode == AlignmentMode.Manual || mode == AlignmentMode.Guided))
    {
        return currentCoordinates;
    }

    var guesses = currentCoordinates.ToList();
    int N = sourceData.Count;
    
    // --- STRUTTURA CORRETTA ---
    // Usiamo una lista di risultati "slot-based" o array per mantenere l'ordine,
    // dato che i task potrebbero finire in tempi diversi.
    Point?[] results = new Point?[N]; 
    
    // Limitiamo la concorrenza per non esplodere la RAM (es. max 4-8 immagini alla volta)
    using var semaphore = new SemaphoreSlim(Environment.ProcessorCount); 

    var processingTasks = new List<Task>();

    // ====================================================================
    // --- STRATEGIA 1: MANUALE ---
    // ====================================================================
    if (mode == AlignmentMode.Manual)
    {
        for (int i = 0; i < N; i++)
        {
            int index = i; // Cattura variabile per la closure
            var guessPoint = guesses[index];
            var fitsData = sourceData[index];

            processingTasks.Add(Task.Run(async () =>
            {
                await semaphore.WaitAsync(); // Attendi slot libero
                try
                {
                    if (guessPoint == null || fitsData == null) 
                    {
                        results[index] = null;
                        return;
                    }

                    using Mat fullImageMat = _processingService.LoadFitsDataAsMat(fitsData);
                    Rect roiRect = CreateSafeRoi(fullImageMat, guessPoint.Value, searchRadius);

                    if (roiRect.Width <= 0 || roiRect.Height <= 0)
                    {
                        results[index] = guessPoint;
                        return;
                    }

                    using Mat regionCrop = new Mat(fullImageMat, roiRect);
                    Point localCenter;

                    switch (method)
                    {
                        case CenteringMethod.Centroid:
                            localCenter = _processingService.GetCenterByCentroid(regionCrop);
                            break;
                        case CenteringMethod.GaussianFit:
                            localCenter = _processingService.GetCenterByGaussianFit(regionCrop);
                            break;
                        case CenteringMethod.Peak:
                            localCenter = _processingService.GetCenterByPeak(regionCrop);
                            break;
                        default: 
                            localCenter = _processingService.GetCenterOfLocalRegion(regionCrop);
                            break;
                    }

                    // Ricostruzione Globale
                    results[index] = new Point(localCenter.X + roiRect.X, localCenter.Y + roiRect.Y);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Manual] Err img {index}: {ex.Message}");
                    results[index] = guessPoint; // Fallback
                }
                finally
                {
                    semaphore.Release(); // Rilascia slot
                }
            }));
        }
        
        await Task.WhenAll(processingTasks);
        return results;
    }

    // ====================================================================
    // --- STRATEGIA 2: AUTOMATICA ---
    // ====================================================================
    else if (mode == AlignmentMode.Automatic)
    {
        for (int i = 0; i < N; i++)
        {
            int index = i;
            var fitsData = sourceData[index];

            processingTasks.Add(Task.Run(async () =>
            {
                await semaphore.WaitAsync();
                try
                {
                    if (fitsData == null) return;

                    using Mat fullImageMat = _processingService.LoadFitsDataAsMat(fitsData);
                    Rect roiRect = new Rect(0, 0, fullImageMat.Width, fullImageMat.Height);
                    using Mat regionCrop = new Mat(fullImageMat, roiRect);

                    // Solo LocalRegion è affidabile su full frame senza guess
                    Point localCenter = _processingService.GetCenterOfLocalRegion(regionCrop);
                    
                    results[index] = new Point(localCenter.X, localCenter.Y);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Auto] Err img {index}: {ex.Message}");
                    results[index] = null;
                }
                finally
                {
                    semaphore.Release();
                }
            }));
        }

        await Task.WhenAll(processingTasks);
        return results;
    }

    // ====================================================================
    // --- STRATEGIA 3: GUIDATA ---
    // ====================================================================
    else if (mode == AlignmentMode.Guided)
    {
        // Nota: Guided ha una logica sequenziale mista a parallela, 
        // qui manteniamo la tua logica ma la incapsuliamo correttamente.
        
        var p1_guess = guesses.FirstOrDefault();
        var pN_guess = guesses.LastOrDefault();

        if (N < 2 || !p1_guess.HasValue || !pN_guess.HasValue || sourceData[0] == null || sourceData[N - 1] == null)
            return currentCoordinates;

        Mat templateF = null;
        Mat tempRawTemplate = null;

        try
        {
            // 1. Prepara Template (Img 0) e Target (Img N)
            var t0 = await RefineAndExtractTemplateButReturnRaw(sourceData[0], p1_guess.Value, searchRadius);
            tempRawTemplate = t0.template;
            Point center1_precise = t0.preciseCenter;
            results[0] = center1_precise; // Salviamo risultato 0

            if (tempRawTemplate == null || tempRawTemplate.Empty()) throw new Exception("Template vuoto");
            templateF = NormalizeAndConvertTo32F(tempRawTemplate);

            var tN = await RefineAndExtractTemplateButReturnRaw(sourceData[N - 1], pN_guess.Value, searchRadius);
            Point centerN_precise = tN.preciseCenter;
            results[N - 1] = centerN_precise; // Salviamo risultato N
            tN.template?.Dispose();

            // 2. Calcola Traiettoria
            double stepX = (centerN_precise.X - center1_precise.X) / (N - 1);
            double stepY = (centerN_precise.Y - center1_precise.Y) / (N - 1);

            // 3. Processa i frame intermedi
            var intermediateTasks = new List<Task>();
            
            for (int i = 1; i < N - 1; i++)
            {
                int index = i;
                var fitsData = sourceData[index];
                double guessX = center1_precise.X + (i * stepX);
                double guessY = center1_precise.Y + (i * stepY);
                Point expectedPoint = new Point(guessX, guessY);

                intermediateTasks.Add(Task.Run(async () =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        if (fitsData == null) 
                        {
                            results[index] = null;
                            return;
                        }

                        using Mat fullImage = _processingService.LoadFitsDataAsMat(fitsData);
                        
                        // A. Area di ricerca estesa
                        int searchW = searchRadius * 3;
                        Rect searchRect = CreateSafeRoi(fullImage, expectedPoint, searchW);

                        // Check bordi per template match
                        if (searchRect.Width <= templateF.Width || searchRect.Height <= templateF.Height)
                        {
                            results[index] = expectedPoint; // Fallback lineare
                            return;
                        }

                        using Mat searchRegion = new Mat(fullImage, searchRect);
                        using Mat searchF = NormalizeAndConvertTo32F(searchRegion);
                        using Mat res = new Mat();

                        // B. Match Template
                        Cv2.MatchTemplate(searchF, templateF, res, TemplateMatchModes.CCoeffNormed);
                        Cv2.MinMaxLoc(res, out _, out double maxVal, out _, out OpenCvSharp.Point maxLoc);

                        if (maxVal < 0.5)
                        {
                            results[index] = expectedPoint; // Match fallito, usa lineare
                            return;
                        }

                        // C. Raffinamento
                        double matchCX = maxLoc.X + (templateF.Width / 2.0);
                        double matchCY = maxLoc.Y + (templateF.Height / 2.0);
                        Point roughLocal = new Point(matchCX, matchCY);
                        
                        Rect refineRect = CreateSafeRoi(searchRegion, roughLocal, searchRadius);
                        using Mat refineCrop = new Mat(searchRegion, refineRect);
                        Point subPixelCenter = _processingService.GetCenterOfLocalRegion(refineCrop);

                        // D. Globalizzazione
                        double globalX = subPixelCenter.X + refineRect.X + searchRect.X;
                        double globalY = subPixelCenter.Y + refineRect.Y + searchRect.Y;
                        
                        results[index] = new Point(globalX, globalY);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[Guided] Crash Img {index}: {ex.Message}");
                        results[index] = expectedPoint;
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }));
            }

            await Task.WhenAll(intermediateTasks);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Guided] Fatal Error: {ex.Message}");
            return currentCoordinates; // Abort guided
        }
        finally
        {
            tempRawTemplate?.Dispose();
            templateF?.Dispose();
        }

        return results;
    }

    // Default fallback se nessuna modalità corrisponde
    return currentCoordinates;
}

    public bool CanCalculate(
        AlignmentMode mode, 
        IEnumerable<Point?> currentCoordinates, 
        int totalCount)
    {
        var coordinateList = currentCoordinates.ToList();
        if (coordinateList.Count == 0)
            return false;
        
        switch (mode)
        {
            case AlignmentMode.Automatic:
                return true; 

            case AlignmentMode.Guided:
                if (totalCount == 1)
                {
                    return coordinateList[0].HasValue;
                }
                
                var hasFirstAndLast = coordinateList.FirstOrDefault().HasValue && 
                                      coordinateList.LastOrDefault().HasValue;
                
                var hasAllGuided = coordinateList.All(e => e.HasValue);
                return hasFirstAndLast || hasAllGuided;

            case AlignmentMode.Manual:
                var hasAllManual = coordinateList.All(e => e.HasValue);
                return hasAllManual;

            default:
                return false;
        }
    }
    
    public async Task<List<FitsImageData?>> ApplyCenteringAsync(List<FitsImageData?> sourceData, List<Point?> centers)
    {
        Size perfectSize = CalculatePerfectCanvasSize(sourceData, centers);
        
        var tasks = sourceData.Select((data, index) => 
            ProcessSingleImageAsync(data, centers, index, perfectSize)
        );
        
        var results = await Task.WhenAll(tasks);
        return results.ToList();
    }

    private Task<FitsImageData?> ProcessSingleImageAsync(FitsImageData? data, List<Point?>? centers, int index, Size targetSize)
    {
        return Task.Run(() =>
        {
            if (data == null) return null;
            var centerPoint = (centers != null && index < centers.Count) ? centers[index] : null;
            if (centerPoint == null) return data;

            try
            {
                using Mat originalMat = _processingService.LoadFitsDataAsMat(data);
                using Mat centeredMat = _processingService.GetSubPixelCenteredCanvas(originalMat, centerPoint.Value, targetSize);
                return _processingService.CreateFitsDataFromMat(centeredMat, data);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error img {index}: {ex.Message}");
                return data;
            }
        });
    }
    
    private Size CalculatePerfectCanvasSize(List<FitsImageData?> sourceData, List<Point?> centers)
    {
        double maxRadiusX = 0;
        double maxRadiusY = 0;

        for (int i = 0; i < sourceData.Count; i++)
        {
            var data = sourceData[i];
            if (data == null || centers == null || i >= centers.Count || centers[i] == null) continue;

            Point center = centers[i]!.Value;
            
            using Mat mat = _processingService.LoadFitsDataAsMat(data);
            Rect validBox = _processingService.FindValidDataBox(mat);

            if (validBox.Width <= 0 || validBox.Height <= 0) continue;
            double distLeft = center.X - validBox.X;
            double distRight = (validBox.X + validBox.Width) - center.X;
            double distTop = center.Y - validBox.Y;
            double distBottom = (validBox.Y + validBox.Height) - center.Y;
            double myRadiusX = Math.Max(distLeft, distRight);
            double myRadiusY = Math.Max(distTop, distBottom);
            
            if (myRadiusX > maxRadiusX) maxRadiusX = myRadiusX;
            if (myRadiusY > maxRadiusY) maxRadiusY = myRadiusY;
        }
        
        int finalW = (int)Math.Ceiling(maxRadiusX * 2);
        int finalH = (int)Math.Ceiling(maxRadiusY * 2);
        
        return (finalW > 0 && finalH > 0) ? new Size(finalW, finalH) : new Size(100, 100);
    }
    
    private async Task<(Mat template, Point preciseCenter)> RefineAndExtractTemplateButReturnRaw(
        FitsImageData fitsData, Point guess, int radius)
    {
        using Mat fullImg = _processingService.LoadFitsDataAsMat(fitsData);
        
        Rect roi = CreateSafeRoi(fullImg, guess, radius);
        if (roi.Width <= 0) throw new Exception("ROI non valida");
        
        // 1. Trova centro preciso usando LocalRegion (Robust Blob Detection)
        using Mat crop = new Mat(fullImg, roi);
        Point local = _processingService.GetCenterOfLocalRegion(crop);
        Point global = new Point(local.X + roi.X, local.Y + roi.Y);
        
        // 2. Estrai Template (Clonato per persistenza)
        Rect templRect = CreateSafeRoi(fullImg, global, radius);
        using Mat tempView = new Mat(fullImg, templRect);
        
        return (tempView.Clone(), global);
    }

    private Mat NormalizeAndConvertTo32F(Mat source)
    {
        Mat floatMat = new Mat();
        // Converte a Float32
        source.ConvertTo(floatMat, MatType.CV_32FC1);
        
        // Normalizza tra 0 e 1 per aiutare MatchTemplate
        Cv2.Normalize(floatMat, floatMat, 0, 1, NormTypes.MinMax);
        
        return floatMat;
    }

    private Rect CreateSafeRoi(Mat mat, Point center, int radius)
    {
        int size = radius * 2;
        int x = (int)(center.X - radius);
        int y = (int)(center.Y - radius);
        int sx = Math.Max(0, x);
        int sy = Math.Max(0, y);
        int sw = Math.Min(size, mat.Width - sx);
        int sh = Math.Min(size, mat.Height - sy);
        return new Rect(sx, sy, sw, sh);
    }

}