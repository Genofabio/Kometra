using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using OpenCvSharp;

namespace KomaLab.Services.Processing.Engines;

public class RadialEnhancementEngine : IRadialEnhancementEngine
{
    private const double TWOPI = 2 * Math.PI;

    // =======================================================================
    // 1. FILTRI GEOMETRICI (Inverse Rho)
    // =======================================================================

    public void ApplyInverseRho(Mat src, Mat dst, int subsampling = 5)
    {
        int rows = src.Rows;
        int cols = src.Cols;
        double xnuc = cols / 2.0;
        double ynuc = rows / 2.0;

        var srcIndexer = src.GetGenericIndexer<float>();
        var dstIndexer = dst.GetGenericIndexer<float>();

        // =========================================================
        // 1. RINORMALIZZAZIONE (Step mancante dal Fortran)
        // =========================================================
        
        // Calcoliamo la media globale dell'immagine
        // Cv2.Mean è ottimizzato e veloce, gestisce internamente la somma
        Scalar meanScalar = Cv2.Mean(src);
        double globalMean = meanScalar.Val0;

        // Fattore di correzione per portare la media a 100.0
        // Se l'immagine è nera (media 0), evitiamo divisione per zero
        float renormFactor = (globalMean > 1e-5) ? (float)(100.0 / globalMean) : 1.0f;

        // =========================================================
        // 2. APPLICAZIONE FILTRO (Con fattore di rinormalizzazione)
        // =========================================================

        double step = 1.0 / subsampling;
        double offsetStart = -0.5 + (step / 2.0);
        
        double[] localOffsets = new double[subsampling];
        for (int i = 0; i < subsampling; i++) localOffsets[i] = offsetStart + (i * step);

        Parallel.For(0, rows, y =>
        {
            double baseDy = ynuc - y;

            for (int x = 0; x < cols; x++)
            {
                float val = srcIndexer[y, x];
                if (float.IsNaN(val))
                {
                    dstIndexer[y, x] = float.NaN;
                    continue;
                }

                // Applichiamo SUBITO la rinormalizzazione al valore del pixel
                float normalizedVal = val * renormFactor;

                double sumRho = 0;
                double baseDx = xnuc - x;

                for (int jj = 0; jj < subsampling; jj++)
                {
                    double dy = baseDy - localOffsets[jj];
                    double dy2 = dy * dy;

                    for (int ii = 0; ii < subsampling; ii++)
                    {
                        double dx = baseDx - localOffsets[ii];
                        sumRho += Math.Sqrt(dx * dx + dy2);
                    }
                }
                
                // NOTA: Il Fortran fa rho = rho * 0.01 (divisione per 100 implicita nel loop 10x10)
                // Poiché noi dividiamo per (subsampling^2), stiamo già facendo la media corretta della distanza.
                // L'unico pezzo che mancava era 'normalizedVal'.
                
                double avgDist = sumRho / (subsampling * subsampling);
                
                // Formula Finale: (Pixel * Renorm) * DistanzaMedia
                dstIndexer[y, x] = normalizedVal * (float)avgDist;
            }
        });
    }

    // =======================================================================
    // 2. TRASFORMAZIONI COORDINATE (Warping) - CORRETTO ARROTONDAMENTO
    // =======================================================================

    // =======================================================================
    // 2. TRASFORMAZIONI COORDINATE (OpenCV Native)
    // =======================================================================

    // =======================================================================
    // 2. TRASFORMAZIONI COORDINATE (OpenCV Native - CORRETTO)
    // =======================================================================

    public Mat ToPolar(Mat src, int nRad, int nTheta, int subsampling = 5)
    {
        Point2f center = new Point2f(src.Cols / 2f, src.Rows / 2f);
        double maxRadius = nRad;

        Mat polarOpenCv = new Mat();

        // FORWARD (Cartesiano -> Polare)
        // Interpolation: Cubic + FillOutliers (per bordi puliti)
        // Mode: Linear
        Cv2.WarpPolar(src, polarOpenCv, new Size(nRad, nTheta), center, maxRadius, 
                      InterpolationFlags.Cubic | InterpolationFlags.WarpFillOutliers, 
                      WarpPolarMode.Linear);

        // Ruotiamo di 90 gradi per avere Raggio sulle Righe (Rows)
        Mat polarCorrected = new Mat();
        Cv2.Rotate(polarOpenCv, polarCorrected, RotateFlags.Rotate90Clockwise);
        
        return polarCorrected;
    }

    public void FromPolar(Mat polar, Mat dst, int width, int height, int subsampling = 5)
    {
        // 1. Riportiamo la matrice polare al formato OpenCV (Raggio su Colonne)
        using Mat polarForOpenCv = new Mat();
        Cv2.Rotate(polar, polarForOpenCv, RotateFlags.Rotate90Counterclockwise);

        Point2f center = new Point2f(width / 2f, height / 2f);
        double maxRadius = polar.Rows; 

        // 2. WARP INVERSO (Polare -> Cartesiano)
        // CORREZIONE QUI:
        // Il flag per invertire è "WarpInverseMap" e sta dentro InterpolationFlags.
        // Il "Mode" rimane Linear (perché stiamo invertendo una trasformazione Linear).
        Cv2.WarpPolar(polarForOpenCv, dst, new Size(width, height), center, maxRadius,
                      InterpolationFlags.Cubic | InterpolationFlags.WarpInverseMap, 
                      WarpPolarMode.Linear);

        // 3. FIX CENTRO (Punto Nero - Stile Fortran)
        // Poiché OpenCV interpola il centro, forziamo manualmente il pixel centrale a 0
        // per evitare artefatti di interpolazione nella singolarità.
        int cx = width / 2;
        int cy = height / 2;
        
        var indexer = dst.GetGenericIndexer<float>();
        indexer[cy, cx] = 0f;
        
        // Opzionale: Se noti ancora un "picco" luminoso spurio proprio nel centro esatto
        // dovuto all'interpolazione bicubica, puoi azzerare anche i 4 vicini.
        // indexer[cy+1, cx] = 0f; indexer[cy-1, cx] = 0f; 
        // indexer[cy, cx+1] = 0f; indexer[cy, cx-1] = 0f;
    }

    // =======================================================================
    // 3. STATISTICA AZIMUTALE (Ottimizzata Memory-Less)
    // =======================================================================

    /// <summary>
    /// Applica la divisione per la Media Azimutale replicando la logica Fortran:
    /// 1. Ignora valori negativi.
    /// 2. Calcola Sigma su due passaggi.
    /// 3. Divisione finale.
    /// </summary>
    public void ApplyAzimuthalAverage(Mat polar, double rejSig)
    {
        int nRad = polar.Rows;
        int nTheta = polar.Cols;
        var indexer = polar.GetGenericIndexer<float>();

        // Usiamo Parallel.For per processare ogni anello (raggio) indipendentemente
        Parallel.For(0, nRad, i =>
        {
            // --- PASSAGGIO 1: Statistiche Iniziali (Media e Sigma Grezzi) ---
            // Il Fortran ignora esplicitamente i valori < 0 (righe 208, 221)
            
            int count = 0;
            double sum = 0;
            double sumSq = 0;

            for (int j = 0; j < nTheta; j++)
            {
                float val = indexer[i, j];
                // Verifica corrispondenza Fortran: if(avect(j).lt.0.0) goto 210
                if (!float.IsNaN(val) && val >= 0) 
                {
                    sum += val;
                    sumSq += val * val;
                    count++;
                }
            }

            // Se non abbiamo abbastanza dati validi, non possiamo filtrare
            if (count < 2) return; 

            double mean1 = sum / count;
            
            // Calcolo Deviazione Standard (Sigma)
            // Vari formula: E[X^2] - (E[X])^2
            double variance = (sumSq / count) - (mean1 * mean1);
            double sigma1 = variance > 0 ? Math.Sqrt(variance) : 0;

            // --- PASSAGGIO 2: Definizione Limiti di Reiezione ---
            // Corrispondenza Fortran righe 225-227
            double rejVal = rejSig * sigma1;
            
            // Fortran clamp: rejmin=max(1.0e-05, (mean-rej))
            double rejMin = Math.Max(1.0e-5, mean1 - rejVal);
            double rejMax = mean1 + rejVal;

            // --- PASSAGGIO 3: Calcolo Media Finale (Senza Outlier) ---
            // Corrispondenza Fortran righe 230-234
            
            double cleanSum = 0;
            int cleanCount = 0;

            for (int j = 0; j < nTheta; j++)
            {
                float val = indexer[i, j];
                // Verifica se il pixel è nel range valido
                if (!float.IsNaN(val) && val >= rejMin && val <= rejMax)
                {
                    cleanSum += val;
                    cleanCount++;
                }
            }

            // Calcolo media finale (se cleanCount è 0, usiamo la media sporca come fallback)
            double finalMean = cleanCount > 0 ? cleanSum / cleanCount : mean1;

            // --- PASSAGGIO 4: Applicazione (Divisione) ---
            // Corrispondenza Fortran riga 242: bvect(j)=avect(j)/(mean+1.0e-6)
            // L'epsilon 1e-6 serve a evitare divisioni per zero se l'anello è nero
            float divisor = (float)(finalMean + 1.0e-6); 

            for (int j = 0; j < nTheta; j++)
            {
                // La divisione si applica anche ai pixel che erano stati esclusi dal calcolo della media!
                // (Es. una stella viene divisa per la media del fondo cielo circostante -> viene attenuata)
                if (!float.IsNaN(indexer[i, j]))
                {
                    indexer[i, j] /= divisor;
                }
            }
        });
    }

    public void ApplyAzimuthalMedian(Mat polar)
    {
        int nRad = polar.Rows;
        int nTheta = polar.Cols;
        var indexer = polar.GetGenericIndexer<float>();

        Parallel.For(0, nRad, i =>
        {
            // ArrayPool per non allocare memoria a ogni giro
            float[] buffer = ArrayPool<float>.Shared.Rent(nTheta);
            int validCount = 0;

            try 
            {
                // 1. RACCOLTA DATI
                // Il Fortran calcola la mediana basandosi sui pixel POSITIVI.
                // Filtriamo subito i negativi (e NaN) per replicare la logica (N + N_neg)/2
                for (int j = 0; j < nTheta; j++)
                {
                    float val = indexer[i, j];
                    // Condizione critica: val >= 0
                    if (!float.IsNaN(val) && val >= 0) 
                    {
                        buffer[validCount++] = val;
                    }
                }

                if (validCount > 0)
                {
                    // 2. ORDINAMENTO
                    // Ordiniamo solo la parte valida (positiva) del buffer
                    Array.Sort(buffer, 0, validCount);
                    
                    // 3. SELEZIONE MEDIANA
                    // Ora prendiamo il centro esatto dei soli valori positivi
                    float median = buffer[validCount / 2];
                    
                    // 4. APPLICAZIONE
                    // Usiamo 1e-6 come il Fortran per stabilità
                    float divisor = median + 1e-6f; 

                    for (int j = 0; j < nTheta; j++)
                    {
                        // La divisione si applica a TUTTI i pixel dell'anello, 
                        // anche quelli negativi che avevamo escluso dal calcolo della mediana.
                        if (!float.IsNaN(indexer[i, j])) 
                        {
                            indexer[i, j] /= divisor;
                        }
                    }
                }
            }
            finally 
            {
                ArrayPool<float>.Shared.Return(buffer);
            }
        });
    }

    public void ApplyAzimuthalRenormalization(Mat polar, double rejSig, double nSig)
    {
        int nRad = polar.Rows;
        int nTheta = polar.Cols;
        var indexer = polar.GetGenericIndexer<float>();

        Parallel.For(0, nRad, i =>
        {
            // --- PASSAGGIO 1: Statistiche Iniziali (Media e Sigma) ---
            int count = 0;
            double sum = 0;
            double sumSq = 0;

            for (int j = 0; j < nTheta; j++)
            {
                float val = indexer[i, j];
                // IMPORTANTE: Fortran ignora i negativi nel calcolo delle statistiche
                if (!float.IsNaN(val) && val >= 0) 
                {
                    sum += val;
                    sumSq += val * val;
                    count++;
                }
            }

            if (count < 3) return;

            double mean1 = sum / count;
            // E[X^2] - (E[X])^2
            double variance = (sumSq / count) - (mean1 * mean1);
            double sigma1 = variance > 0 ? Math.Sqrt(variance) : 0;

            // --- PASSAGGIO 2: Sigma Clipping ---
            // Fortran logic: rej = rejsig * sigma
            double rejVal = rejSig * sigma1;
            double rejMin = Math.Max(1.0e-5, mean1 - rejVal);
            double rejMax = mean1 + rejVal;
            
            double cleanSum = 0;
            double cleanSumSq = 0; // Serve per ricalcolare sigma
            int cleanCount = 0;

            for (int j = 0; j < nTheta; j++)
            {
                float val = indexer[i, j];
                // Qui filtriamo sia per NaN che per range
                if (!float.IsNaN(val) && val >= rejMin && val <= rejMax)
                {
                    cleanSum += val;
                    cleanSumSq += val * val;
                    cleanCount++;
                }
            }

            if (cleanCount < 2) return; // Fallback: lascia invariato

            double finalMean = cleanSum / cleanCount;
            double finalVar = (cleanSumSq / cleanCount) - (finalMean * finalMean);
            double finalStd = finalVar > 0 ? Math.Sqrt(finalVar) : 0;

            // --- PASSAGGIO 3: Normalizzazione (Stretch) ---
            // Fortran mappa [Mean - nSig*Std, Mean + nSig*Std] a [0, 255].
            // Noi produciamo un output float centrato su 0 (Z-Score).
            // Formula: (Val - Mean) / (Std * nSig)
            // Se Val == Mean -> 0. Se Val == Max -> 1.0. Se Val == Min -> -1.0.
            
            float divisor = (float)(finalStd * nSig);
            if (divisor < 1e-9f) divisor = 1.0f;

            for (int j = 0; j < nTheta; j++)
            {
                float val = indexer[i, j];
                if (!float.IsNaN(val))
                {
                    // Risultato in unità di "Sigma scalato".
                    // Questo preserva la dinamica floating point meglio del clamp a 0-255.
                    indexer[i, j] = (val - (float)finalMean) / divisor;
                }
            }
        });
    }
}