using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;

namespace Kometra.Services.Processing.Engines.Enhancement;

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

        // Se il kernel è grande, usiamo l'implementazione parallela sicura per la memoria e ottimizzata
        if (k > 5) {
            await _memSemaphore.WaitAsync();
            try {
                await Task.Run(() => {
                    // Dispatching dinamico per supportare sia Float che Double
                    if (src.Depth() == MatType.CV_64F) CustomMedianBlurSafeDouble(src, blurred, k, progress);
                    else CustomMedianBlurSafeFloat(src, blurred, k, progress);
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

    private void CustomMedianBlurSafeDouble(Mat src, Mat dst, int k, IProgress<double> progress)
    {
        int rows = src.Rows;
        int cols = src.Cols;
        int r = k / 2;
        int kernelArea = k * k;

        src.GetArray(out double[] sArr);
        double[] dArr = new double[sArr.Length];
        int completed = 0;

        Parallel.For(0, rows, (int y) =>
        {
            // Usiamo ArrayPool per evitare di allocare milioni di array
            double[] buffer = ArrayPool<double>.Shared.Rent(kernelArea);
            try
            {
                int rowOffset = y * cols;
                int yMinInit = Math.Max(0, y - r);
                int yMaxInit = Math.Min(rows - 1, y + r);

                for (int x = 0; x < cols; x++)
                {
                    int xMin = Math.Max(0, x - r);
                    int xMax = Math.Min(cols - 1, x + r);
                    int count = 0;

                    for (int ky = yMinInit; ky <= yMaxInit; ky++)
                    {
                        int kRowOffset = ky * cols;
                        for (int kx = xMin; kx <= xMax; kx++)
                        {
                            double val = sArr[kRowOffset + kx];
                            // Ignoriamo i NaN nel calcolo della mediana per sicurezza
                            if (!double.IsNaN(val) && !double.IsInfinity(val))
                            {
                                buffer[count++] = val;
                            }
                        }
                    }

                    if (count > 0)
                    {
                        // QuickSelect trova la mediana istantaneamente
                        dArr[rowOffset + x] = QuickSelectDouble(buffer, 0, count - 1, count / 2);
                    }
                    else
                    {
                        dArr[rowOffset + x] = 0.0;
                    }
                }
            }
            finally
            {
                ArrayPool<double>.Shared.Return(buffer);
            }

            if (progress != null)
            {
                var current = Interlocked.Increment(ref completed);
                if (current % 50 == 0) progress.Report((double)current / rows * 100);
            }
        });

        dst.Create(src.Size(), src.Type());
        dst.SetArray(dArr);
    }

    private void CustomMedianBlurSafeFloat(Mat src, Mat dst, int k, IProgress<double> progress)
    {
        int rows = src.Rows;
        int cols = src.Cols;
        int r = k / 2;
        int kernelArea = k * k;

        src.GetArray(out float[] sArr);
        float[] dArr = new float[sArr.Length];
        int completed = 0;

        Parallel.For(0, rows, (int y) =>
        {
            float[] buffer = ArrayPool<float>.Shared.Rent(kernelArea);
            try
            {
                int rowOffset = y * cols;
                int yMinInit = Math.Max(0, y - r);
                int yMaxInit = Math.Min(rows - 1, y + r);

                for (int x = 0; x < cols; x++)
                {
                    int xMin = Math.Max(0, x - r);
                    int xMax = Math.Min(cols - 1, x + r);
                    int count = 0;

                    for (int ky = yMinInit; ky <= yMaxInit; ky++)
                    {
                        int kRowOffset = ky * cols;
                        for (int kx = xMin; kx <= xMax; kx++)
                        {
                            float val = sArr[kRowOffset + kx];
                            if (!float.IsNaN(val) && !float.IsInfinity(val))
                            {
                                buffer[count++] = val;
                            }
                        }
                    }

                    if (count > 0)
                    {
                        dArr[rowOffset + x] = QuickSelectFloat(buffer, 0, count - 1, count / 2);
                    }
                    else
                    {
                        dArr[rowOffset + x] = 0f;
                    }
                }
            }
            finally
            {
                ArrayPool<float>.Shared.Return(buffer);
            }

            if (progress != null)
            {
                var current = Interlocked.Increment(ref completed);
                if (current % 50 == 0) progress.Report((double)current / rows * 100);
            }
        });

        dst.Create(src.Size(), src.Type());
        dst.SetArray(dArr);
    }

    // =========================================================
    // ALGORITMI DI QUICK-SELECT (SELEZIONE LINEARE O(N))
    // =========================================================

    private static double QuickSelectDouble(double[] arr, int left, int right, int k)
    {
        while (left < right)
        {
            int pivotIndex = PartitionDouble(arr, left, right);
            if (pivotIndex == k) return arr[k];
            else if (k < pivotIndex) right = pivotIndex - 1;
            else left = pivotIndex + 1;
        }
        return arr[k];
    }

    private static int PartitionDouble(double[] arr, int left, int right)
    {
        double pivot = arr[right];
        int i = left - 1;
        for (int j = left; j < right; j++)
        {
            if (arr[j] <= pivot)
            {
                i++;
                double temp = arr[i];
                arr[i] = arr[j];
                arr[j] = temp;
            }
        }
        double temp2 = arr[i + 1];
        arr[i + 1] = arr[right];
        arr[right] = temp2;
        return i + 1;
    }

    private static float QuickSelectFloat(float[] arr, int left, int right, int k)
    {
        while (left < right)
        {
            int pivotIndex = PartitionFloat(arr, left, right);
            if (pivotIndex == k) return arr[k];
            else if (k < pivotIndex) right = pivotIndex - 1;
            else left = pivotIndex + 1;
        }
        return arr[k];
    }

    private static int PartitionFloat(float[] arr, int left, int right)
    {
        float pivot = arr[right];
        int i = left - 1;
        for (int j = left; j < right; j++)
        {
            if (arr[j] <= pivot)
            {
                i++;
                float temp = arr[i];
                arr[i] = arr[j];
                arr[j] = temp;
            }
        }
        float temp2 = arr[i + 1];
        arr[i + 1] = arr[right];
        arr[right] = temp2;
        return i + 1;
    }

// =========================================================
    // 2. ADAPTIVE LOCAL NORMALIZATION (LSN) - GAUSSIAN 
    // =========================================================
    public async Task ApplyLocalNormalizationAsync(Mat src, Mat dst, int windowSize, double intensity, IProgress<double> progress = null)
    {
        // Il kernel deve essere dispari per la convoluzione Gaussiana
        int k = windowSize % 2 == 0 ? windowSize + 1 : windowSize;
        Size kSize = new Size(k, k);
        
        // Lasciamo calcolare la Sigma ottimale a OpenCV in base alla dimensione k
        double sigma = 0; 

        await Task.Run(() => {
            // 1. SANITIZZAZIONE DEI DATI (Safe & Managed)
            using Mat srcClean = new Mat(src.Size(), src.Type());
            using Mat weights = new Mat(src.Size(), src.Type());

            if (src.Depth() == MatType.CV_64F)
                SanitizeMapSafe<double>(src, srcClean, weights);
            else
                SanitizeMapSafe<float>(src, srcClean, weights);

            // 2. CALCOLO DEI PESI LOCALI (Count)
            using Mat localWeight = new Mat();
            Cv2.GaussianBlur(weights, localWeight, kSize, sigma, sigma, BorderTypes.Replicate);
            Cv2.Max(localWeight, Scalar.All(1e-9), localWeight);

            // 3. CALCOLO MEDIA (Mean)
            using Mat sumRaw = new Mat();
            Cv2.GaussianBlur(srcClean, sumRaw, kSize, sigma, sigma, BorderTypes.Replicate);
            
            using Mat mean = new Mat();
            Cv2.Divide(sumRaw, localWeight, mean);

            // 4. CALCOLO DEVIAZIONE STANDARD (StdDev)
            using Mat srcSq = new Mat();
            Cv2.Multiply(srcClean, srcClean, srcSq);
            
            using Mat sumSqRaw = new Mat();
            Cv2.GaussianBlur(srcSq, sumSqRaw, kSize, sigma, sigma, BorderTypes.Replicate);
            
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
            Cv2.Max(stdDev, Scalar.All(1e-9), stdDev); // Evita div/0 (Versione originale)

            // 5. NORMALIZZAZIONE FINALE
            // dst = (srcClean - mean) / stdDev
            using Mat diff = new Mat();
            Cv2.Subtract(srcClean, mean, diff);
            Cv2.Divide(diff, stdDev, dst);

            // Applicazione intensità
            if (Math.Abs(intensity - 1.0) > double.Epsilon) 
                Cv2.Multiply(dst, Scalar.All(intensity), dst);
            
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