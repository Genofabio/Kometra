using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KomaLab.Services.Processing.Engines;

public class StructureExtractionEngine : IStructureExtractionEngine
{
    // Semaforo asincrono per evitare di saturare la memoria senza bloccare i thread
    private readonly SemaphoreSlim _memSemaphore = new SemaphoreSlim(Math.Max(1, Environment.ProcessorCount / 2));

    // =========================================================
    // 1. LARSON-SEKANINA (Versione Async)
    // =========================================================
    public async Task ApplyLarsonSekaninaAsync(Mat src, Mat dst, double angleDeg, double radialShiftX, double radialShiftY, bool isSymmetric)
    {
        // Task.Run sposta l'esecuzione fuori dal thread UI
        await Task.Run(() =>
        {
            dst.Create(src.Size(), src.Type());
            Point2f center = new Point2f(src.Cols / 2f, src.Rows / 2f);

            Mat GetRotatedImage(Mat input, double deg, double sx, double sy)
            {
                using Mat rotMat = Cv2.GetRotationMatrix2D(center, deg, 1.0);
                var indexer = rotMat.GetGenericIndexer<double>();
                indexer[0, 2] += sx;
                indexer[1, 2] += sy;

                Mat res = new Mat();
                Cv2.WarpAffine(input, res, rotMat, input.Size(), InterpolationFlags.Cubic, BorderTypes.Constant, Scalar.All(0));
                return res;
            }

            if (isSymmetric)
            {
                using Mat rotPlus = GetRotatedImage(src, angleDeg, radialShiftX, radialShiftY);
                using Mat rotMinus = GetRotatedImage(src, -angleDeg, -radialShiftX, -radialShiftY);
                using Mat sumRot = new Mat();
                Cv2.Add(rotPlus, rotMinus, sumRot); 
                Cv2.ScaleAdd(src, 2.0, -sumRot, dst);
            }
            else
            {
                using Mat rot = GetRotatedImage(src, angleDeg, radialShiftX, radialShiftY);
                Cv2.Subtract(src, rot, dst);
            }

            using Mat mask = new Mat(src.Size(), MatType.CV_8UC1, Scalar.All(255));
            using Mat maskRotated = new Mat();

            if (isSymmetric)
            {
                using Mat mPlus = GetRotatedImage(mask, angleDeg, radialShiftX, radialShiftY);
                using Mat mMinus = GetRotatedImage(mask, -angleDeg, -radialShiftX, -radialShiftY);
                Cv2.BitwiseAnd(mPlus, mMinus, maskRotated);
            }
            else
            {
                using (Mat m = GetRotatedImage(mask, angleDeg, radialShiftX, radialShiftY))
                    m.CopyTo(maskRotated);
            }

            using Mat finalMask = new Mat();
            maskRotated.ConvertTo(finalMask, dst.Type(), 1.0 / 255.0);
            Cv2.Multiply(dst, finalMask, dst);
        });
    }

    // =========================================================
    // 2. UNSHARP MASKING MEDIAN (Versione Async)
    // =========================================================
    public async Task ApplyUnsharpMaskingMedianAsync(Mat src, Mat dst, int kernelSize, IProgress<double> progress = null)
    {
        int k = kernelSize % 2 == 0 ? kernelSize + 1 : kernelSize;
        if (k < 1) k = 1;

        dst.Create(src.Size(), src.Type());
        using Mat blurred = new Mat();

        bool needCustomSolver = (src.Depth() == MatType.CV_32F || src.Depth() == MatType.CV_64F) && k > 5;

        if (needCustomSolver)
        {
            // WaitAsync non blocca la UI mentre aspetta il semaforo
            await _memSemaphore.WaitAsync();
            try
            {
                await Task.Run(() => {
                    if (src.Depth() == MatType.CV_64F) CustomMedianBlurSafe<double>(src, blurred, k, progress);
                    else CustomMedianBlurSafe<float>(src, blurred, k, progress);
                });
            }
            finally { _memSemaphore.Release(); }
        }
        else
        {
            await Task.Run(() => Cv2.MedianBlur(src, blurred, k));
            progress?.Report(100);
        }

        Cv2.Subtract(src, blurred, dst);
    }

    private void CustomMedianBlurSafe<T>(Mat src, Mat dst, int k, IProgress<double> progress) where T : struct, IComparable<T>
    {
        dst.Create(src.Size(), src.Type());
        int rows = src.Rows; int cols = src.Cols;
        int radius = k / 2; int windowSize = k * k; int midIndex = windowSize / 2;

        var srcIdx = src.GetGenericIndexer<T>();
        var dstIdx = dst.GetGenericIndexer<T>();
        int completedRows = 0;

        using var localWindow = new ThreadLocal<T[]>(() => new T[windowSize]);

        Parallel.For(0, rows, y =>
        {
            T[] window = localWindow.Value;
            for (int x = 0; x < cols; x++)
            {
                int count = 0;
                for (int ky = -radius; ky <= radius; ky++)
                {
                    int ny = Math.Clamp(y + ky, 0, rows - 1);
                    for (int kx = -radius; kx <= radius; kx++)
                    {
                        int nx = Math.Clamp(x + kx, 0, cols - 1);
                        window[count++] = srcIdx[ny, nx];
                    }
                }
                Array.Sort(window);
                dstIdx[y, x] = window[midIndex];
            }
            
            // Aggiorna il progresso ogni 50 righe per non intasare il bus della UI
            var current = Interlocked.Increment(ref completedRows);
            if (current % 50 == 0) progress?.Report((double)current / rows * 100);
        });
    }

    // =========================================================
    // 3. ADAPTIVE RVSF (Versione Async)
    // =========================================================
    public async Task ApplyAdaptiveRVSFAsync(Mat src, Mat dst, double paramA, double paramB, double paramN, bool useLog, IProgress<double> progress = null)
    {
        await _memSemaphore.WaitAsync();
        try
        {
            await Task.Run(() =>
            {
                using Mat workImg = PrepareImageForRVSF(src, useLog);
                dst.Create(src.Size(), workImg.Type());

                if (workImg.Depth() == MatType.CV_64F) RunRVSFCore<double>(workImg, dst, paramA, paramB, paramN, progress);
                else RunRVSFCore<float>(workImg, dst, paramA, paramB, paramN, progress);
            });
        }
        finally { _memSemaphore.Release(); }
    }

    public async Task ApplyRVSFMosaicAsync(Mat src, Mat dst, (double v1, double v2) paramA, (double v1, double v2) paramB, (double v1, double v2) paramN, bool useLog)
    {
        using Mat workImg = PrepareImageForRVSF(src, useLog);
        int w = src.Cols; int h = src.Rows;

        dst.Create(new Size(w * 4, h * 2), workImg.Type());
        dst.SetTo(Scalar.All(0));

        double[] as_vals = { paramA.v1, paramA.v2 };
        double[] bs_vals = { paramB.v1, paramB.v2 };
        double[] ns_vals = { paramN.v1, paramN.v2 };

        var combinations = new List<(double a, double b, double n)>();
        foreach (var a in as_vals) foreach (var b in bs_vals) foreach (var n in ns_vals) combinations.Add((a, b, n));

        // Eseguiamo i tasselli in parallelo, gestendo il semaforo asincrono
        var tasks = new List<Task>();
        for (int i = 0; i < combinations.Count; i++)
        {
            int index = i;
            tasks.Add(Task.Run(async () =>
            {
                await _memSemaphore.WaitAsync();
                try
                {
                    var (a, b, n) = combinations[index];
                    int gridRow = index / 4;
                    int gridCol = index % 4;
                    using Mat subDst = new Mat(dst, new Rect(gridCol * w, gridRow * h, w, h));
                    
                    if (workImg.Depth() == MatType.CV_64F) RunRVSFCore<double>(workImg, subDst, a, b, n, null);
                    else RunRVSFCore<float>(workImg, subDst, a, b, n, null);
                }
                finally { _memSemaphore.Release(); }
            }));
        }
        await Task.WhenAll(tasks);
    }

    private void RunRVSFCore<T>(Mat src, Mat dst, double A, double B, double N, IProgress<double> progress) where T : struct
    {
        int rows = src.Rows; int cols = src.Cols;
        double xNuc = cols / 2.0; double yNuc = rows / 2.0;
        var srcIdx = src.GetGenericIndexer<T>();
        var dstIdx = dst.GetGenericIndexer<T>();
        int completedRows = 0;

        Parallel.For(0, rows, y =>
        {
            double dy = y - yNuc;
            double dySq = dy * dy;

            for (int x = 0; x < cols; x++)
            {
                double dx = x - xNuc;
                double rho = Math.Sqrt(dx * dx + dySq);
                int r = (int)Math.Round(A + (B * Math.Pow(rho, N)));
                if (r < 1) r = 1;

                int yMin = Math.Clamp(y - r, 0, rows - 1);
                int yMax = Math.Clamp(y + r, 0, rows - 1);
                int xMin = Math.Clamp(x - r, 0, cols - 1);
                int xMax = Math.Clamp(x + r, 0, cols - 1);

                if (typeof(T) == typeof(double))
                {
                    double cur = (double)(object)srcIdx[y, x];
                    double sE = (double)(object)srcIdx[yMin, x] + (double)(object)srcIdx[yMax, x] + (double)(object)srcIdx[y, xMin] + (double)(object)srcIdx[y, xMax];
                    double sC = (double)(object)srcIdx[yMin, xMin] + (double)(object)srcIdx[yMin, xMax] + (double)(object)srcIdx[yMax, xMin] + (double)(object)srcIdx[yMax, xMax];
                    dstIdx[y, x] = (T)(object)((1024.0 * cur) - (192.0 * sE) - (64.0 * sC));
                }
                else
                {
                    float cur = (float)(object)srcIdx[y, x];
                    float sE = (float)(object)srcIdx[yMin, x] + (float)(object)srcIdx[yMax, x] + (float)(object)srcIdx[y, xMin] + (float)(object)srcIdx[y, xMax];
                    float sC = (float)(object)srcIdx[yMin, xMin] + (float)(object)srcIdx[yMin, xMax] + (float)(object)srcIdx[yMax, xMin] + (float)(object)srcIdx[yMax, xMax];
                    dstIdx[y, x] = (T)(object)((1024f * cur) - (192f * sE) - (64f * sC));
                }
            }
            var current = Interlocked.Increment(ref completedRows);
            if (current % 100 == 0) progress?.Report((double)current / rows * 100);
        });
    }

    private Mat PrepareImageForRVSF(Mat src, bool useLog)
    {
        if (!useLog) return src.Clone();
        Mat work = new Mat();
        src.ConvertTo(work, MatType.CV_32F);
        Cv2.Add(work, Scalar.All(1.0), work);
        Cv2.Log(work, work);
        Cv2.Multiply(work, Scalar.All(0.4342944819), work);
        return work;
    }
}