using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KomaLab.Models.Primitives;
using KomaLab.Services.Fits;
using OpenCvSharp;
// Namespace corretto per IFitsIoService

namespace KomaLab.Services.Processing.AlignmentStrategies;

// ---------------------------------------------------------------------------
// FILE: StarAlignmentStrategy.cs
// RUOLO: Strategia Allineamento (Deep Sky)
// DESCRIZIONE:
// Strategia ibrida per l'allineamento su campo stellare.
//
// MODALITÀ OPERATIVE:
// 1. WCS Priority: Se i dati di Plate Solving sono disponibili (passati come 'guesses'),
//    vengono usati direttamente (massima precisione e velocità).
// 2. Visual Fallback (FFT): Se mancano i dati WCS, calcola lo spostamento (dx, dy)
//    tra frame consecutivi usando la Phase Correlation (FFT) sulle stelle.
// ---------------------------------------------------------------------------

public class StarAlignmentStrategy : IAlignmentStrategy
{
    private readonly IFitsIoService _ioService;      // Sostituisce il vecchio FitsService
    private readonly IFitsImageDataConverter _converter;
    private readonly IImageAnalysisService _analysis;

    public StarAlignmentStrategy(
        IFitsIoService ioService, 
        IFitsImageDataConverter converter, 
        IImageAnalysisService analysis)
    {
        _ioService = ioService ?? throw new ArgumentNullException(nameof(ioService));
        _converter = converter ?? throw new ArgumentNullException(nameof(converter));
        _analysis = analysis ?? throw new ArgumentNullException(nameof(analysis));
    }

    public async Task<Point2D?[]> CalculateAsync(
        List<string> sourcePaths, 
        List<Point2D?> guesses, 
        int searchRadius, 
        IProgress<(int Index, Point2D? Center)>? progress)
    {
        int n = sourcePaths.Count;
        var results = new Point2D?[n];

        // Se il primo frame ha un guess valido, assumiamo che il ViewModel ci abbia passato dati WCS.
        // Questo attiva la modalità "WCS Priority".
        bool hasWcsInput = (guesses.Count > 0 && guesses[0].HasValue);

        if (hasWcsInput)
        {
            // --- MODALITÀ 1: WCS PRIORITARIO (FIDUCIA NELL'INPUT) ---
            
            // Il primo frame è l'ancora
            results[0] = guesses[0];
            progress?.Report((0, results[0]));

            for (int i = 1; i < n; i++)
            {
                // CASO A: Abbiamo il dato WCS per questo frame -> Usalo (Veloce e preciso)
                if (i < guesses.Count && guesses[i].HasValue)
                {
                    results[i] = guesses[i];
                }
                // CASO B: Buco nei dati WCS -> Fallback visuale (FFT) sul frame precedente
                else
                {
                    try
                    {
                        var dataPrev = await _ioService.LoadAsync(sourcePaths[i - 1]);
                        var dataCurr = await _ioService.LoadAsync(sourcePaths[i]);

                        if (dataPrev != null && dataCurr != null)
                        {
                            using var matPrev = _converter.RawToMat(dataPrev);
                            using var matCurr = _converter.RawToMat(dataCurr);

                            // Calcola lo spostamento (dx, dy) tra i due frame usando FFT
                            Point2D shift = await Task.Run(() => _analysis.ComputeStarFieldShift(matPrev, matCurr));

                            Point2D prevCenter = results[i - 1] ?? new Point2D(0, 0);
                            results[i] = new Point2D(prevCenter.X + shift.X, prevCenter.Y + shift.Y);
                        }
                        else
                        {
                            // Se fallisce il caricamento, manteniamo la posizione precedente (0 shift)
                            results[i] = results[i - 1]; 
                        }
                    }
                    catch 
                    { 
                        // Fallback estremo in caso di errore
                        results[i] = results[i - 1]; 
                    }
                }
                progress?.Report((i, results[i]));
            }
        }
        else
        {
            // --- MODALITÀ 2: FFT PURA (VISUALE / BLIND) ---
            // Nessun dato WCS. Allineiamo tutto basandoci sul centro geometrico del primo frame
            // e calcolando gli shift successivi in sequenza.

            // 1. Inizializzazione Frame 0 (Centro geometrico immagine)
            try 
            {
                // Usiamo ReadHeaderOnlyAsync per leggere le dimensioni velocemente
                var header = await _ioService.ReadHeaderOnlyAsync(sourcePaths[0]);
                if (header != null)
                {
                    double w = header.GetIntValue("NAXIS1");
                    double h = header.GetIntValue("NAXIS2");
                    
                    results[0] = new Point2D(w / 2.0, h / 2.0);
                    progress?.Report((0, results[0]));
                }
                else return results; // Impossibile procedere senza dimensioni
            }
            catch { return results; }

            // 2. Loop Sequenziale FFT
            // Manteniamo in memoria la matrice precedente per confrontarla con la corrente.
            // Questo riduce il carico I/O del 50% rispetto al ricaricare sempre i file.
            Mat? prevMat = null;
            try 
            {
                var data0 = await _ioService.LoadAsync(sourcePaths[0]);
                if (data0 != null) prevMat = _converter.RawToMat(data0);
            } 
            catch { /* Ignora errori init */ }

            if (prevMat == null) return results; // Fallito caricamento frame 0

            try
            {
                for (int i = 1; i < n; i++)
                {
                    Mat? currentMat = null;
                    try
                    {
                        var dataCurr = await _ioService.LoadAsync(sourcePaths[i]);
                        if (dataCurr != null)
                        {
                            currentMat = _converter.RawToMat(dataCurr);

                            // Calcolo shift relativo tra Prev e Curr
                            Point2D shift = await Task.Run(() => _analysis.ComputeStarFieldShift(prevMat, currentMat));

                            Point2D prevCenter = results[i - 1]!.Value;
                            results[i] = new Point2D(prevCenter.X + shift.X, prevCenter.Y + shift.Y);
                            
                            // --- SWAP DELLE MATRICI (Gestione Memoria) ---
                            // 1. Buttiamo via la vecchia matrice 'prevMat' (non serve più per il prossimo confronto)
                            prevMat.Dispose();
                            
                            // 2. La 'currentMat' diventa la nuova 'prevMat' per il prossimo giro del loop.
                            // Trasferiamo il riferimento, quindi NON dobbiamo fare Dispose di currentMat qui.
                            prevMat = currentMat; 
                            
                            // 3. Annulliamo il riferimento locale per evitare che il blocco finally la distrugga
                            currentMat = null; 
                        }
                        else
                        {
                            // Se il caricamento fallisce, assumiamo shift zero
                            results[i] = results[i - 1];
                        }
                        progress?.Report((i, results[i]));
                    }
                    catch
                    {
                        results[i] = results[i - 1];
                    }
                    finally
                    {
                        // Se currentMat è ancora != null, significa che qualcosa è fallito PRIMA dello swap.
                        // Dobbiamo pulire per evitare memory leak.
                        currentMat?.Dispose();
                    }
                }
            }
            finally
            {
                // Pulizia finale della matrice rimasta in memoria alla fine del loop
                prevMat.Dispose();
            }
        }

        return results;
    }
}