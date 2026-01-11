using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KomaLab.Models.Fits;
using KomaLab.Models.Primitives;
using KomaLab.Services.Data; // Namespace corretto per IFitsIoService
using OpenCvSharp;

namespace KomaLab.Services.Imaging.AlignmentStrategies;

// ---------------------------------------------------------------------------
// FILE: GuidedCometAlignmentStrategy.cs
// RUOLO: Strategia Allineamento (Tracking)
// DESCRIZIONE:
// Allinea le immagini su un oggetto in movimento (cometa/asteroide) usando
// un approccio ibrido:
// 1. Predizione: Stima la posizione usando interpolazione lineare tra Start/End
//    o dati esterni (es. lista 'guesses' pre-popolata da JPL).
// 2. Correzione: Esegue un Template Matching locale attorno alla stima per
//    centrare esattamente il nucleo della cometa.
// ---------------------------------------------------------------------------

public class GuidedCometAlignmentStrategy : IAlignmentStrategy
{
    private readonly IFitsIoService _ioService;      // Sostituisce il vecchio FitsService
    private readonly IFitsImageDataConverter _converter;
    private readonly IImageOperationService _operations; // Servizio per Template Matching

    public GuidedCometAlignmentStrategy(
        IFitsIoService ioService, 
        IFitsImageDataConverter converter, 
        IImageOperationService operations)
    {
        _ioService = ioService ?? throw new ArgumentNullException(nameof(ioService));
        _converter = converter ?? throw new ArgumentNullException(nameof(converter));
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
    }

    public async Task<Point2D?[]> CalculateAsync(
        List<string> sourcePaths, 
        List<Point2D?> guesses, 
        int searchRadius, 
        IProgress<(int Index, Point2D? Center)>? progress)
    {
        int n = sourcePaths.Count;
        var results = new Point2D?[n];

        // Validazione Input: Servono almeno Frame 0 e Frame N (o input utente su questi)
        var p1Guess = guesses.FirstOrDefault();
        var pNGuess = guesses.LastOrDefault();

        if (n < 2 || !p1Guess.HasValue || !pNGuess.HasValue) 
        {
            // Senza estremi, non possiamo interpolare. Ritorniamo quello che abbiamo.
            return guesses.ToArray();
        }

        // Configurazione Concorrenza (per evitare OutOfMemory su batch grandi)
        long firstFileSize = 0;
        try { if (n > 0) firstFileSize = new FileInfo(sourcePaths[0]).Length; } catch { }
        
        int maxConcurrency;
        if (firstFileSize > 100 * 1024 * 1024) maxConcurrency = 1; 
        else if (firstFileSize > 20 * 1024 * 1024) maxConcurrency = Math.Clamp(Environment.ProcessorCount / 2, 2, 3);
        else maxConcurrency = Math.Clamp(Environment.ProcessorCount, 2, 4);

        using var semaphore = new SemaphoreSlim(maxConcurrency);
        Mat? templateMat = null;

        try
        {
            // --- FASE 1: Inizializzazione (Frame 0) ---
            // Carichiamo il primo frame per estrarre il "Modello" (Template) della cometa
            var data0 = await _ioService.LoadAsync(sourcePaths[0]);
            if (data0 == null) return results;

            // Estrazione Template: Ritagliamo un box attorno al punto iniziale.
            // _operations.ExtractRefinedTemplate si occupa di normalizzare e preparare il piccolo Mat.
            var t0 = _operations.ExtractRefinedTemplate(data0, p1Guess.Value, searchRadius);
            templateMat = t0.Template; 
            
            // Impostiamo il risultato del primo frame
            Point2D centerStart = p1Guess.Value;
            results[0] = centerStart;
            progress?.Report((0, centerStart));

            // Impostiamo il risultato dell'ultimo frame
            Point2D centerEnd = pNGuess.Value;
            results[n - 1] = centerEnd;
            progress?.Report((n - 1, centerEnd));

            // --- FASE 2: Calcolo Traiettoria (Lineare) ---
            double stepX = (centerEnd.X - centerStart.X) / (n - 1);
            double stepY = (centerEnd.Y - centerStart.Y) / (n - 1);

            // --- FASE 3: Tracking (Parallelo) ---
            var tasks = new List<Task>();

            for (int i = 1; i < n - 1; i++)
            {
                int index = i;
                string path = sourcePaths[index];

                // A. Calcolo Posizione Attesa (Priority: NASA Guess > Interpolazione Lineare)
                Point2D expectedPoint;
                if (index < guesses.Count && guesses[index].HasValue)
                {
                    expectedPoint = guesses[index]!.Value;
                }
                else
                {
                    double linX = centerStart.X + (index * stepX);
                    double linY = centerStart.Y + (index * stepY);
                    expectedPoint = new Point2D(linX, linY);
                }

                tasks.Add(Task.Run(async () =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        var fitsData = await _ioService.LoadAsync(path);
                        if (fitsData == null) 
                        { 
                            results[index] = null; 
                            return; 
                        }

                        using Mat fullImage = _converter.RawToMat(fitsData);

                        // B. Template Matching Locale
                        // Cerchiamo il templateMat dentro fullImage, ma SOLO nel raggio di 'searchRadius'
                        // attorno a 'expectedPoint'. Questo corregge gli errori della traiettoria lineare.
                        Point2D? foundMatch = _operations.FindTemplatePosition(
                            fullImage, 
                            templateMat, 
                            expectedPoint, 
                            searchRadius);
                            
                        // Se il matching fallisce (es. nuvola), usiamo la stima lineare come fallback.
                        var finalPoint = foundMatch ?? expectedPoint;
                        
                        results[index] = finalPoint;
                        progress?.Report((index, finalPoint));
                    }
                    catch
                    {
                        // Fallback difensivo
                        results[index] = expectedPoint;
                        progress?.Report((index, expectedPoint));
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }));
            }

            await Task.WhenAll(tasks);
        }
        finally
        {
            // Importante: Dispose del template Mat creato manualmente
            templateMat?.Dispose();
        }

        return results;
    }
}