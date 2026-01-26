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
    // 2. ADAPTIVE LOCAL NORMALIZATION (LSN)
    // =========================================================
    public async Task ApplyLocalNormalizationAsync(Mat src, Mat dst, int windowSize, double intensity, IProgress<double> progress = null)
    {
        await Task.Run(() => {
            dst.Create(src.Size(), src.Type());
            int k = windowSize | 1;

            using Mat mean = new Mat();
            using Mat srcSq = new Mat();
            using Mat meanSq = new Mat();
            using Mat stdDev = new Mat();

            // Calcolo media locale e stdDev locale senza mai uscire dai float/double
            // OpenCV gestisce automaticamente il tipo di dato (32F o 64F) di 'src'
            Cv2.BoxFilter(src, mean, src.Type(), new Size(k, k), new Point(-1, -1), true, BorderTypes.Replicate);
            Cv2.Multiply(src, src, srcSq);
            Cv2.BoxFilter(srcSq, meanSq, src.Type(), new Size(k, k), new Point(-1, -1), true, BorderTypes.Replicate);

            using Mat meanSqDiff = new Mat();
            Cv2.Multiply(mean, mean, meanSqDiff);
            Cv2.Subtract(meanSq, meanSqDiff, stdDev);
            Cv2.Max(stdDev, Scalar.All(1e-15), stdDev); // Evita divisione per zero
            Cv2.Sqrt(stdDev, stdDev);

            // dst = (src - mean) / (stdDev * intensity)
            using Mat diff = new Mat();
            Cv2.Subtract(src, mean, diff);
            Cv2.Divide(diff, stdDev, dst);
            if (intensity != 1.0) Cv2.Multiply(dst, Scalar.All(intensity), dst);
            
            progress?.Report(100);
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