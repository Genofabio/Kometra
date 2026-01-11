using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using KomaLab.Models.Primitives;
using KomaLab.Models.Processing;
using KomaLab.Services.Data; // Namespace per IFitsIoService
using OpenCvSharp;

namespace KomaLab.Services.Imaging.AlignmentStrategies;

// ---------------------------------------------------------------------------
// FILE: ManualCometAlignmentStrategy.cs
// RUOLO: Strategia Allineamento (Raffinamento ROI)
// DESCRIZIONE:
// Strategia per l'allineamento basato su input utente (Point & Click).
// Non si limita a usare il punto cliccato, ma ritaglia una piccola area (ROI)
// attorno ad esso e calcola il centroide matematico per garantire precisione sub-pixel.
// ---------------------------------------------------------------------------

public class ManualCometAlignmentStrategy : IAlignmentStrategy
{
    private readonly IFitsIoService _ioService;      // Sostituisce il vecchio FitsService
    private readonly IFitsImageDataConverter _converter;
    private readonly IImageAnalysisService _analysis;
    private readonly CenteringMethod _method; // Algoritmo scelto (Gaussian, Centroid, ecc.)

    public ManualCometAlignmentStrategy(
        IFitsIoService ioService, 
        IFitsImageDataConverter converter, 
        IImageAnalysisService analysis,
        CenteringMethod method)
    {
        _ioService = ioService ?? throw new ArgumentNullException(nameof(ioService));
        _converter = converter ?? throw new ArgumentNullException(nameof(converter));
        _analysis = analysis ?? throw new ArgumentNullException(nameof(analysis));
        _method = method;
    }

    public async Task<Point2D?[]> CalculateAsync(
        List<string> sourcePaths, 
        List<Point2D?> guesses, 
        int searchRadius, 
        IProgress<(int Index, Point2D? Center)>? progress)
    {
        int n = sourcePaths.Count;
        var results = new Point2D?[n];

        // Configurazione Concorrenza
        long firstFileSize = 0;
        try { if (n > 0) firstFileSize = new FileInfo(sourcePaths[0]).Length; } catch { }
        
        int maxConcurrency = (firstFileSize > 100 * 1024 * 1024) 
            ? 1 
            : Math.Clamp(Environment.ProcessorCount / 2, 2, 4);

        using var semaphore = new SemaphoreSlim(maxConcurrency);
        var tasks = new List<Task>();

        for (int i = 0; i < n; i++)
        {
            int index = i;
            string path = sourcePaths[index];
            
            // In modalità manuale il GUESS è OBBLIGATORIO (è il click dell'utente).
            // Se manca per un frame (es. utente ha saltato un frame), quel frame non viene allineato.
            var guess = (index < guesses.Count) ? guesses[index] : null;

            if (guess == null) 
            {
                results[index] = null;
                continue;
            }

            tasks.Add(Task.Run(async () =>
            {
                await semaphore.WaitAsync();
                try
                {
                    // Tenta di raffinare il punto cliccato cercando il picco luminoso vicino
                    Point2D? result = await RefineCenterInRoiAsync(path, guess.Value, searchRadius);
                    
                    results[index] = result;
                    progress?.Report((index, result));
                }
                finally { semaphore.Release(); }
            }));
        }

        await Task.WhenAll(tasks);
        return results;
    }

    private async Task<Point2D?> RefineCenterInRoiAsync(string path, Point2D guess, int radius)
    {
        try
        {
            // 1. Caricamento Dati (Resilienza I/O gestita dal provider)
            var fitsData = await _ioService.LoadAsync(path);
            if (fitsData == null) return guess; // Fallback al click grezzo

            using var fullMat = _converter.RawToMat(fitsData);

            // 2. Calcolo Coordinate ROI (Region Of Interest)
            // Definiamo un quadrato di lato (radius * 2) centrato sul guess
            int size = radius * 2;
            int x = (int)(guess.X - radius);
            int y = (int)(guess.Y - radius);
            
            // Clipping sicuro sui bordi dell'immagine (evita crash OpenCV)
            int roiX = Math.Max(0, x);
            int roiY = Math.Max(0, y);
            int roiW = Math.Min(fullMat.Width, x + size) - roiX;
            int roiH = Math.Min(fullMat.Height, y + size) - roiY;

            // Se la ROI è degenere (troppo piccola o fuori immagine), ritorniamo il click originale
            if (roiW <= 4 || roiH <= 4) return guess;

            // 3. Analisi Locale
            // Creiamo una "vista" (sotto-matrice) senza copiare dati se possibile
            using var roiMat = new Mat(fullMat, new Rect(roiX, roiY, roiW, roiH));
            
            Point2D localCenter;
            switch (_method)
            {
                case CenteringMethod.Centroid: 
                    localCenter = _analysis.FindCentroid(roiMat); 
                    break;
                case CenteringMethod.GaussianFit: 
                    localCenter = _analysis.FindGaussianCenter(roiMat); 
                    break;
                case CenteringMethod.Peak: 
                    localCenter = _analysis.FindPeak(roiMat); 
                    break;
                default: 
                    localCenter = _analysis.FindCenterOfLocalRegion(roiMat); 
                    break;
            }
            
            // 4. Trasformazione Coordinate (Locale -> Globale)
            return new Point2D(localCenter.X + roiX, localCenter.Y + roiY);
        }
        catch (Exception)
        {
            // Se qualcosa va storto nell'analisi (es. NaN, immagine nera),
            // fidiamoci del click dell'utente piuttosto che non ritornare nulla.
            return guess;
        }
    }
}