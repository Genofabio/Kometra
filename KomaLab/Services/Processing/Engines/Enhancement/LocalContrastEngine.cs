using System;
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;

namespace KomaLab.Services.Processing.Engines.Enhancement;

public class LocalContrastEngine : ILocalContrastEngine
{
    private readonly SemaphoreSlim _memSemaphore = new SemaphoreSlim(Math.Max(1, Environment.ProcessorCount / 2));

    // =========================================================
    // 1. UNSHARP MASKING MEDIAN
    // =========================================================
    public async Task ApplyUnsharpMaskingMedianAsync(Mat src, Mat dst, int kernelSize, IProgress<double> progress = null)
    {
        int k = kernelSize % 2 == 0 ? kernelSize + 1 : kernelSize;
        if (k < 1) k = 1;
        dst.Create(src.Size(), src.Type());
        using Mat blurred = new Mat();

        // Se il kernel è grande, usiamo l'implementazione parallela sicura per la memoria
        if (k > 5) {
            await _memSemaphore.WaitAsync();
            try {
                await Task.Run(() => {
                    // Dispatching dinamico per supportare sia Float che Double
                    if (src.Depth() == MatType.CV_64F) CustomMedianBlurSafe<double>(src, blurred, k, progress);
                    else CustomMedianBlurSafe<float>(src, blurred, k, progress);
                });
            } finally { _memSemaphore.Release(); }
        }
        else {
            // Per kernel piccoli, l'implementazione OpenCV è più veloce
            await Task.Run(() => Cv2.MedianBlur(src, blurred, k));
            progress?.Report(100);
        }
        
        // Unsharp Mask = Original - Blurred
        Cv2.Subtract(src, blurred, dst);
    }

    // =========================================================
    // 2. ADAPTIVE LOCAL NORMALIZATION (LSN) - NaN SAFE
    // =========================================================
    public async Task ApplyLocalNormalizationAsync(Mat src, Mat dst, int windowSize, double intensity, IProgress<double> progress = null)
{
    // Calcoliamo parametri kernel
    int k = windowSize % 2 == 0 ? windowSize + 1 : windowSize;
    Size kSize = new Size(k, k);
    Point anchor = new Point(-1, -1);
    BorderTypes border = BorderTypes.Replicate;

    await Task.Run(() => {
        // 1. SANITIZZAZIONE DEI DATI (Safe & Managed)
        // Creiamo due matrici pulite:
        // - srcClean: contiene i pixel originali, ma i NaN sono sostituiti da 0.0
        // - weights: contiene 1.0 se il pixel è valido, 0.0 se era NaN
        
        using Mat srcClean = new Mat(src.Size(), src.Type());
        using Mat weights = new Mat(src.Size(), src.Type());

        // Usiamo un metodo helper con Generic Indexer (NO UNSAFE)
        if (src.Depth() == MatType.CV_64F)
            SanitizeMapSafe<double>(src, srcClean, weights);
        else
            SanitizeMapSafe<float>(src, srcClean, weights);

        // DA QUI IN POI, OpenCV VEDE SOLO 0 e 1. I NaN SONO SPARITI.
        
        // 2. CALCOLO DEI PESI LOCALI (Count)
        // Quanti pixel validi ci sono in ogni finestra k*k?
        using Mat localWeight = new Mat();
        Cv2.BoxFilter(weights, localWeight, src.Type(), kSize, anchor, true, border);
        // Evitiamo divisione per zero (se una zona è tutta NaN, mettiamo un epsilon)
        Cv2.Max(localWeight, Scalar.All(1e-9), localWeight);

        // 3. CALCOLO MEDIA (Mean)
        // BoxFilter(Clean) fa la somma (perché i NaN sono 0). 
        // Dividendo per localWeight otteniamo la media reale dei soli pixel validi.
        using Mat sumRaw = new Mat();
        Cv2.BoxFilter(srcClean, sumRaw, src.Type(), kSize, anchor, true, border);
        
        using Mat mean = new Mat();
        Cv2.Divide(sumRaw, localWeight, mean);

        // 4. CALCOLO DEVIAZIONE STANDARD (StdDev)
        // Calcoliamo i quadrati su srcClean (0^2 = 0)
        using Mat srcSq = new Mat();
        Cv2.Multiply(srcClean, srcClean, srcSq);
        
        using Mat sumSqRaw = new Mat();
        Cv2.BoxFilter(srcSq, sumSqRaw, src.Type(), kSize, anchor, true, border);
        
        using Mat meanSq = new Mat();
        Cv2.Divide(sumSqRaw, localWeight, meanSq);

        // Varianza = MeanSq - Mean^2
        using Mat stdDev = new Mat();
        using Mat meanSqDiff = new Mat();
        Cv2.Multiply(mean, mean, meanSqDiff);
        Cv2.Subtract(meanSq, meanSqDiff, stdDev);

        // Correzione errori numerici (variance non può essere negativa)
        Cv2.Max(stdDev, Scalar.All(0), stdDev);
        Cv2.Sqrt(stdDev, stdDev);
        Cv2.Max(stdDev, Scalar.All(1e-9), stdDev); // Evita div/0

        // 5. NORMALIZZAZIONE FINALE
        // dst = (srcClean - mean) / stdDev
        // NOTA FONDAMENTALE: Usiamo srcClean, non src!
        // - Se il pixel era valido: (Valore - Media) / StdDev -> OK
        // - Se il pixel era NaN (ora 0): (0 - Media) / StdDev -> Valore calcolato (Inpainting)
        
        using Mat diff = new Mat();
        Cv2.Subtract(srcClean, mean, diff);
        Cv2.Divide(diff, stdDev, dst);

        // Applicazione intensità
        if (Math.Abs(intensity - 1.0) > double.Epsilon) 
            Cv2.Multiply(dst, Scalar.All(intensity), dst);
        
        // OPZIONALE: Ripristinare i NaN originali nell'output?
        // Se vuoi che l'output abbia NaN dove l'input aveva NaN, scommenta queste righe:
        /*
        using Mat nanMask = new Mat();
        Cv2.Compare(weights, Scalar.All(0), nanMask, CmpTypes.EQ);
        dst.SetTo(Scalar.All(double.NaN), nanMask);
        */

        progress?.Report(100);
    });
}

// Helper Safe che usa GetGenericIndexer invece dei puntatori unsafe
private void SanitizeMapSafe<T>(Mat src, Mat clean, Mat weights) where T : struct, IEquatable<T>
{
    // Otteniamo gli indexer per accesso veloce (ma safe) ai pixel
    var srcIdx = src.GetGenericIndexer<T>();
    var cleanIdx = clean.GetGenericIndexer<T>();
    var wIdx = weights.GetGenericIndexer<T>();

    int rows = src.Rows;
    int cols = src.Cols;

    // Parallelizziamo il ciclo per mantenere le performance alte
    Parallel.For(0, rows, y =>
    {
        for (int x = 0; x < cols; x++)
        {
            T val = srcIdx[y, x];
            bool isNan;

            // Controllo NaN specifico per tipo
            if (typeof(T) == typeof(double))
                isNan = double.IsNaN(Convert.ToDouble(val));
            else
                isNan = float.IsNaN(Convert.ToSingle(val));

            if (isNan)
            {
                // Trovato NaN: pulisci (0) e peso nullo (0)
                cleanIdx[y, x] = default(T); // 0
                wIdx[y, x] = default(T);     // 0
            }
            else
            {
                // Valido: copia valore e peso unitario (1)
                cleanIdx[y, x] = val;
                
                // Assegnazione 1.0 generica
                if (typeof(T) == typeof(double))
                    wIdx[y, x] = (T)(object)1.0;
                else
                    wIdx[y, x] = (T)(object)1.0f;
            }
        }
    });
}

    // =========================================================
    // 3. CLAHE
    // =========================================================
    public async Task ApplyClaheAsync(Mat src, Mat dst, double clipLimit, int tileGridSize)
    {
        await Task.Run(() => {
            // Nota: OpenCV richiede obbligatoriamente 8U o 16U per CLAHE.
            // Convertiamo temporaneamente a 16-bit (0-65535)
            using Mat work16 = new Mat();
            Cv2.Normalize(src, work16, 0, 65535, NormTypes.MinMax, MatType.CV_16U);

            using var clahe = Cv2.CreateCLAHE(clipLimit, new Size(tileGridSize, tileGridSize));
            using Mat res16 = new Mat();
            clahe.Apply(work16, res16);

            // Riconvertiamo al formato originale (Float/Double) normalizzato 0.0-1.0 (o scala originale)
            res16.ConvertTo(dst, src.Type(), 1.0 / 65535.0);
        });
    }

    // =========================================================
    // PRIVATE HELPERS
    // =========================================================
    
    private void CustomMedianBlurSafe<T>(Mat src, Mat dst, int k, IProgress<double> progress) where T : struct, IComparable<T>
    {
        dst.Create(src.Size(), src.Type());
        int rows = src.Rows; int cols = src.Cols;
        int radius = k / 2; int windowSize = k * k; int midIndex = windowSize / 2;
        
        var srcIdx = src.GetGenericIndexer<T>(); 
        var dstIdx = dst.GetGenericIndexer<T>();
        
        int completed = 0;
        
        // ThreadLocal buffer per evitare allocazioni eccessive nel loop parallelo
        using var localWindow = new ThreadLocal<T[]>(() => new T[windowSize]);
        
        Parallel.For(0, rows, y => {
            T[] window = localWindow.Value;
            for (int x = 0; x < cols; x++) {
                int count = 0;
                // Raccolta kernel
                for (int ky = -radius; ky <= radius; ky++) {
                    int ny = Math.Clamp(y + ky, 0, rows - 1);
                    for (int kx = -radius; kx <= radius; kx++) {
                        int nx = Math.Clamp(x + kx, 0, cols - 1);
                        window[count++] = srcIdx[ny, nx];
                    }
                }
                // Calcolo Mediana
                Array.Sort(window);
                dstIdx[y, x] = window[midIndex];
            }
            
            var current = Interlocked.Increment(ref completed);
            if (progress != null && current % 50 == 0) progress.Report((double)current / rows * 100);
        });
    }
}