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
        // 1. Uscita rapida se non ci sono parametri validi
        if (searchRadius <= 0 && (mode == AlignmentMode.Manual || mode == AlignmentMode.Guided))
        {
            return currentCoordinates;
        }

        var calculateCentersAsync = currentCoordinates.ToList();
        var guesses = calculateCentersAsync.ToList();
        int n = sourceData.Count;
        
        // Array per mantenere l'ordine dei risultati anche con esecuzione parallela
        Point?[] results = new Point?[n]; 
        
        // Semaforo per limitare l'uso della CPU/RAM (es. elabora max N core alla volta)
        int maxConcurrency = Math.Min(4, Environment.ProcessorCount);
        using var semaphore = new SemaphoreSlim(maxConcurrency); 
        var processingTasks = new List<Task>();

        // ====================================================================
        // --- STRATEGIA 1: MANUALE ---
        // ====================================================================
        if (mode == AlignmentMode.Manual)
        {
            for (int i = 0; i < n; i++)
            {
                int index = i; // Cattura indice per la closure
                var guessPoint = guesses[index];
                var fitsData = sourceData[index];

                processingTasks.Add(Task.Run(async () =>
                {
                    await semaphore.WaitAsync(); 
                    try
                    {
                        if (guessPoint == null || fitsData == null) 
                        {
                            results[index] = null;
                            return;
                        }

                        using Mat fullImageMat = _processingService.LoadFitsDataAsMat(fitsData);
                        
                        // Calcolo ROI sicuro (senza funzione helper privata, logica geometrica inline)
                        int size = searchRadius * 2;
                        int x = (int)(guessPoint.Value.X - searchRadius);
                        int y = (int)(guessPoint.Value.Y - searchRadius);
                        Rect imageBounds = new Rect(0, 0, fullImageMat.Width, fullImageMat.Height);
                        Rect targetRect = new Rect(x, y, size, size);
                        Rect roiRect = imageBounds.Intersect(targetRect);

                        if (roiRect.Width <= 0 || roiRect.Height <= 0)
                        {
                            results[index] = guessPoint; // ROI fuori dai bordi, mantieni guess
                            return;
                        }

                        using Mat regionCrop = new Mat(fullImageMat, roiRect);
                        Point localCenter;

                        // Delega al service il calcolo specifico
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
                            default: // Robust Local Region
                                localCenter = _processingService.GetCenterOfLocalRegion(regionCrop);
                                break;
                        }

                        // Ricostruzione coordinate globali
                        results[index] = new Point(localCenter.X + roiRect.X, localCenter.Y + roiRect.Y);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[Manual] Err img {index}: {ex.Message}");
                        results[index] = guessPoint; // Fallback su errore
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
        // --- STRATEGIA 2: AUTOMATICA ---
        // ====================================================================
        else if (mode == AlignmentMode.Automatic)
        {
            for (int i = 0; i < n; i++)
            {
                int index = i;
                var fitsData = sourceData[index];

                processingTasks.Add(Task.Run(async () =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        if (fitsData == null) 
                        {
                            results[index] = null;
                            return;
                        }

                        using Mat fullImageMat = _processingService.LoadFitsDataAsMat(fitsData);
                        
                        // In automatico usiamo l'intera immagine
                        // GetCenterOfLocalRegion è l'unico abbastanza robusto per blind search
                        Point center = _processingService.GetCenterOfLocalRegion(fullImageMat);
                        
                        results[index] = center;
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
        // --- STRATEGIA 3: GUIDATA (REFECTORED SRP) ---
        // ====================================================================
        else if (mode == AlignmentMode.Guided)
        {
            var p1Guess = guesses.FirstOrDefault();
            var pNGuess = guesses.LastOrDefault();

            // Controllo preliminare requisiti Guided
            if (n < 2 || !p1Guess.HasValue || !pNGuess.HasValue || sourceData[0] == null || sourceData[n - 1] == null)
                return calculateCentersAsync;

            Mat? templateMat = null;

            try
            {
                // 1. Estrai Template Raffinato dalla prima immagine (Delegato al Service)
                var t0 = _processingService.ExtractRefinedTemplate(sourceData[0], p1Guess.Value, searchRadius);
                templateMat = t0.template;
                Point center1Precise = t0.preciseCenter;
                results[0] = center1Precise; 

                // 2. Trova centro preciso dell'ultima immagine (solo per calcolare la traiettoria)
                // Non serve conservare il template dell'ultima, usiamo Dispose immediato se restituito
                var tN = _processingService.ExtractRefinedTemplate(sourceData[n - 1], pNGuess.Value, searchRadius);
                Point centerNPrecise = tN.preciseCenter;
                results[n - 1] = centerNPrecise;
                tN.template.Dispose(); 

                // 3. Calcola vettore traiettoria (shift per frame)
                double stepX = (centerNPrecise.X - center1Precise.X) / (n - 1);
                double stepY = (centerNPrecise.Y - center1Precise.Y) / (n - 1);

                // 4. Elaborazione parallela dei frame intermedi
                var intermediateTasks = new List<Task>();
                
                for (int i = 1; i < n - 1; i++)
                {
                    int index = i;
                    var fitsData = sourceData[index];
                    
                    // Calcola la posizione stimata linearmente
                    double guessX = center1Precise.X + (i * stepX);
                    double guessY = center1Precise.Y + (i * stepY);
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
                            
                            // CHIAMATA SRP: Il service cerca il template vicino al punto atteso
                            Point? foundMatch = _processingService.FindTemplatePosition(
                                fullImage, 
                                templateMat, 
                                expectedPoint, 
                                searchRadius
                            );

                            // Se il match fallisce (null), usiamo la stima lineare come fallback
                            results[index] = foundMatch ?? expectedPoint;
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[Guided] Crash Img {index}: {ex.Message}");
                            results[index] = expectedPoint; // Fallback su errore
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
                return calculateCentersAsync; // In caso di errore grave, ritorna coordinate originali
            }
            finally
            {
                // Importante: rilasciare il template che abbiamo tenuto vivo per tutto il loop
                templateMat?.Dispose();
            }

            return results;
        }

        // Fallback finale
        return calculateCentersAsync;
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

}