using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KomaLab.Services.Processing.Engines;

public class StructureExtractionEngine : IStructureExtractionEngine
{
    private readonly SemaphoreSlim _memSemaphore = new SemaphoreSlim(Math.Max(1, Environment.ProcessorCount / 2));

    // =========================================================
    // 1. LARSON-SEKANINA (Rotational Gradient) - 100% Precision
    // =========================================================
    public async Task ApplyLarsonSekaninaAsync(Mat src, Mat dst, double angleDeg, double radialShiftX, double radialShiftY, bool isSymmetric)
    {
        await Task.Run(() =>
        {
            dst.Create(src.Size(), src.Type());
            Point2f center = new Point2f(src.Cols / 2f, src.Rows / 2f);

            Mat GetRotatedImage(Mat input, double deg, double sx, double sy)
            {
                using Mat rotMat = Cv2.GetRotationMatrix2D(center, deg, 1.0);
                var indexer = rotMat.GetGenericIndexer<double>();
                indexer[0, 2] += sx; indexer[1, 2] += sy;
                Mat res = new Mat();
                // Cubic interpolation preserva la qualità dei dati floating point
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
                using (Mat m = GetRotatedImage(mask, angleDeg, radialShiftX, radialShiftY)) m.CopyTo(maskRotated);
            }

            using Mat finalMask = new Mat();
            maskRotated.ConvertTo(finalMask, dst.Type(), 1.0 / 255.0);
            Cv2.Multiply(dst, finalMask, dst);
        });
    }

    // =========================================================
    // 2. UNSHARP MASKING MEDIAN
    // =========================================================
    public async Task ApplyUnsharpMaskingMedianAsync(Mat src, Mat dst, int kernelSize, IProgress<double> progress = null)
    {
        int k = kernelSize % 2 == 0 ? kernelSize + 1 : kernelSize;
        if (k < 1) k = 1;
        dst.Create(src.Size(), src.Type());
        using Mat blurred = new Mat();

        if (k > 5)
        {
            await _memSemaphore.WaitAsync();
            try {
                await Task.Run(() => {
                    if (src.Depth() == MatType.CV_64F) CustomMedianBlurSafe<double>(src, blurred, k, progress);
                    else CustomMedianBlurSafe<float>(src, blurred, k, progress);
                });
            } finally { _memSemaphore.Release(); }
        }
        else
        {
            await Task.Run(() => Cv2.MedianBlur(src, blurred, k));
            progress?.Report(100);
        }
        Cv2.Subtract(src, blurred, dst);
    }

    // =========================================================
    // 3. ADAPTIVE RVSF (Radial Variable Slope Filter)
    // =========================================================
    public async Task ApplyAdaptiveRVSFAsync(Mat src, Mat dst, double paramA, double paramB, double paramN, bool useLog, IProgress<double> progress = null)
    {
        await _memSemaphore.WaitAsync();
        try {
            await Task.Run(() => {
                using Mat workImg = PrepareImageForRVSF(src, useLog);
                dst.Create(src.Size(), workImg.Type());
                if (workImg.Depth() == MatType.CV_64F) RunRVSFCore<double>(workImg, dst, paramA, paramB, paramN, progress);
                else RunRVSFCore<float>(workImg, dst, paramA, paramB, paramN, progress);
            });
        } finally { _memSemaphore.Release(); }
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

        var tasks = new List<Task>();
        for (int i = 0; i < combinations.Count; i++)
        {
            int index = i;
            tasks.Add(Task.Run(async () =>
            {
                await _memSemaphore.WaitAsync();
                try {
                    var (a, b, n) = combinations[index];
                    int gridRow = index / 4; int gridCol = index % 4;
                    using Mat subDst = new Mat(dst, new Rect(gridCol * w, gridRow * h, w, h));
                    if (workImg.Depth() == MatType.CV_64F) RunRVSFCore<double>(workImg, subDst, a, b, n, null);
                    else RunRVSFCore<float>(workImg, subDst, a, b, n, null);
                } finally { _memSemaphore.Release(); }
            }));
        }
        await Task.WhenAll(tasks);
    }

    // =========================================================
    // 4. FRANGI VESSELNESS FILTER (Hessiana 64-bit)
    // =========================================================
    public async Task ApplyFrangiVesselnessAsync(Mat src, Mat dst, double sigma, double beta, double c, IProgress<double> progress = null)
    {
        await _memSemaphore.WaitAsync();
        try
        {
            await Task.Run(() =>
            {
                dst.Create(src.Size(), src.Type());
                using Mat work = new Mat();
                src.ConvertTo(work, MatType.CV_64F); 

                using Mat dxx = new Mat(); using Mat dyy = new Mat(); using Mat dxy = new Mat();
                int kSize = (int)Math.Ceiling(sigma * 3) * 2 + 1;
                Cv2.GaussianBlur(work, work, new Size(kSize, kSize), sigma);
                Cv2.Sobel(work, dxx, MatType.CV_64F, 2, 0, 3);
                Cv2.Sobel(work, dyy, MatType.CV_64F, 0, 2, 3);
                Cv2.Sobel(work, dxy, MatType.CV_64F, 1, 1, 3);

                if (src.Depth() == MatType.CV_64F) RunFrangiCore<double>(src, dxx, dyy, dxy, dst, beta, c);
                else RunFrangiCore<float>(src, dxx, dyy, dxy, dst, beta, c);
                progress?.Report(100);
            });
        }
        finally { _memSemaphore.Release(); }
    }

    // =========================================================
    // 5. CLAHE (Adaptive Histogram Equalization - 16-bit)
    // =========================================================
    public async Task ApplyClaheAsync(Mat src, Mat dst, double clipLimit, int tileGridSize)
    {
        await Task.Run(() =>
        {
            // Nota: OpenCV richiede obbligatoriamente 8U o 16U per CLAHE.
            using Mat work16 = new Mat();
            Cv2.Normalize(src, work16, 0, 65535, NormTypes.MinMax, MatType.CV_16U);

            // tileGridSize definisce il numero di celle (es. 8x8).
            using var clahe = Cv2.CreateCLAHE(clipLimit, new Size(tileGridSize, tileGridSize));
            using Mat res16 = new Mat();
            clahe.Apply(work16, res16);

            res16.ConvertTo(dst, src.Type(), 1.0 / 65535.0);
        });
    }

    // =========================================================
    // 6. ADAPTIVE LOCAL NORMALIZATION (LSN) - 100% Float/Double
    // =========================================================
    public async Task ApplyLocalNormalizationAsync(Mat src, Mat dst, int windowSize, double intensity, IProgress<double> progress = null)
    {
        await Task.Run(() =>
        {
            dst.Create(src.Size(), src.Type());
            int k = windowSize | 1;

            using Mat mean = new Mat();
            using Mat srcSq = new Mat();
            using Mat meanSq = new Mat();
            using Mat stdDev = new Mat();

            // Calcolo media locale e stdDev locale senza mai uscire dai float
            Cv2.BoxFilter(src, mean, src.Type(), new Size(k, k), new Point(-1, -1), true, BorderTypes.Replicate);
            Cv2.Multiply(src, src, srcSq);
            Cv2.BoxFilter(srcSq, meanSq, src.Type(), new Size(k, k), new Point(-1, -1), true, BorderTypes.Replicate);

            using Mat meanSqDiff = new Mat();
            Cv2.Multiply(mean, mean, meanSqDiff);
            Cv2.Subtract(meanSq, meanSqDiff, stdDev);
            Cv2.Max(stdDev, Scalar.All(1e-15), stdDev); 
            Cv2.Sqrt(stdDev, stdDev);

            // dst = (src - mean) / (stdDev * intensity)
            // L'immagine potrebbe apparire nera solo se il WhitePoint del renderer
            // non viene ricalcolato sulle nuove statistiche (media 0).
            using Mat diff = new Mat();
            Cv2.Subtract(src, mean, diff);
            Cv2.Divide(diff, stdDev, dst);
            if (intensity != 1.0) Cv2.Multiply(dst, Scalar.All(intensity), dst);
            
            progress?.Report(100);
        });
    }

    // =========================================================
    // 7. WHITE TOP-HAT (Morfologia - Float Safe)
    // =========================================================
    public async Task ApplyWhiteTopHatAsync(Mat src, Mat dst, int kernelSize)
    {
        await Task.Run(() =>
        {
            using Mat blurred = new Mat();
            // Uno smoothing di sigma 0.8 - 1.0 pulisce i "falsi getti" da rumore
            Cv2.GaussianBlur(src, blurred, new Size(3, 3), 0.8);
        
            int k = Math.Max(3, kernelSize | 1); 
            using Mat element = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(k, k));
        
            Cv2.MorphologyEx(blurred, dst, MorphTypes.TopHat, element);
            Cv2.Max(dst, Scalar.All(0), dst);
        });
    }

    // =========================================================
    // 8. STRUCTURE TENSOR (Coerenza - 100% Float Safe)
    // =========================================================
    public async Task ApplyStructureTensorEnhancementAsync(Mat src, Mat dst, int sigma, int rho, IProgress<double> progress = null)
    {
        await _memSemaphore.WaitAsync();
        try
        {
            await Task.Run(() =>
            {
                dst.Create(src.Size(), src.Type());
                using Mat dx = new Mat(); using Mat dy = new Mat();
                Cv2.Sobel(src, dx, src.Type(), 1, 0, 3);
                Cv2.Sobel(src, dy, src.Type(), 0, 1, 3);

                using Mat dxx = new Mat(); using Mat dyy = new Mat(); using Mat dxy = new Mat();
                Cv2.Multiply(dx, dx, dxx); Cv2.Multiply(dy, dy, dyy); Cv2.Multiply(dx, dy, dxy);

                int kSize = (rho * 2) | 1;
                Cv2.GaussianBlur(dxx, dxx, new Size(kSize, kSize), sigma);
                Cv2.GaussianBlur(dyy, dyy, new Size(kSize, kSize), sigma);
                Cv2.GaussianBlur(dxy, dxy, new Size(kSize, kSize), sigma);

                if (src.Depth() == MatType.CV_64F) RunTensorCoherenceCore<double>(src, dxx, dyy, dxy, dst);
                else RunTensorCoherenceCore<float>(src, dxx, dyy, dxy, dst);
                progress?.Report(100);
            });
        } finally { _memSemaphore.Release(); }
    }

    // =========================================================
    // KERNELS CORE
    // =========================================================

    private void RunTensorCoherenceCore<T>(Mat src, Mat dxx, Mat dyy, Mat dxy, Mat dst) where T : struct
    {
        var idxXX = dxx.GetGenericIndexer<T>(); var idxYY = dyy.GetGenericIndexer<T>();
        var idxXY = dxy.GetGenericIndexer<T>(); var idxSrc = src.GetGenericIndexer<T>();
        var idxDst = dst.GetGenericIndexer<T>();

        Parallel.For(0, src.Rows, y => {
            for (int x = 0; x < src.Cols; x++) {
                double j11 = Convert.ToDouble(idxXX[y, x]);
                double j22 = Convert.ToDouble(idxYY[y, x]);
                double j12 = Convert.ToDouble(idxXY[y, x]);
                double trace = j11 + j22;
                double det = j11 * j22 - j12 * j12;
                double sqrtTerm = Math.Sqrt(Math.Max(0, Math.Pow(trace / 2.0, 2) - det));
                double l1 = (trace / 2.0) + sqrtTerm;
                double l2 = (trace / 2.0) - sqrtTerm;
                double coherence = (l1 + l2) > 1e-12 ? Math.Pow((l1 - l2) / (l1 + l2), 2) : 0;
                idxDst[y, x] = (T)Convert.ChangeType(Convert.ToDouble(idxSrc[y, x]) * coherence, typeof(T));
            }
        });
    }

    private void RunFrangiCore<T>(Mat src, Mat dxx, Mat dyy, Mat dxy, Mat dst, double beta, double c) where T : struct
    {
        var idxXX = dxx.GetGenericIndexer<double>();
        var idxYY = dyy.GetGenericIndexer<double>();
        var idxXY = dxy.GetGenericIndexer<double>();
        var idxDst = dst.GetGenericIndexer<T>();
        double betaSq = 2.0 * beta * beta;
        double cSq = 2.0 * c * c;

        Parallel.For(0, src.Rows, y => {
            for (int x = 0; x < src.Cols; x++) {
                double fxx = idxXX[y, x]; double fyy = idxYY[y, x]; double fxy = idxXY[y, x];
                double trace = fxx + fyy;
                double det = fxx * fyy - fxy * fxy;
                double disc = Math.Sqrt(Math.Max(0, trace * trace - 4.0 * det));
                double l1 = (trace + disc) / 2.0; double l2 = (trace - disc) / 2.0;
                if (Math.Abs(l1) > Math.Abs(l2)) (l1, l2) = (l2, l1);
                if (l2 >= 0) { idxDst[y, x] = default; continue; }
                double Rb = Math.Abs(l1) / Math.Abs(l2); 
                double S = Math.Sqrt(l1 * l1 + l2 * l2); 
                double vesselness = Math.Exp(-(Rb * Rb) / betaSq) * (1.0 - Math.Exp(-(S * S) / cSq));
                idxDst[y, x] = (T)Convert.ChangeType(vesselness, typeof(T));
            }
        });
    }

    private void CustomMedianBlurSafe<T>(Mat src, Mat dst, int k, IProgress<double> progress) where T : struct, IComparable<T>
    {
        dst.Create(src.Size(), src.Type());
        int rows = src.Rows; int cols = src.Cols;
        int radius = k / 2; int windowSize = k * k; int midIndex = windowSize / 2;
        var srcIdx = src.GetGenericIndexer<T>(); var dstIdx = dst.GetGenericIndexer<T>();
        int completed = 0;
        using var localWindow = new ThreadLocal<T[]>(() => new T[windowSize]);
        Parallel.For(0, rows, y => {
            T[] window = localWindow.Value;
            for (int x = 0; x < cols; x++) {
                int count = 0;
                for (int ky = -radius; ky <= radius; ky++) {
                    int ny = Math.Clamp(y + ky, 0, rows - 1);
                    for (int kx = -radius; kx <= radius; kx++) {
                        int nx = Math.Clamp(x + kx, 0, cols - 1);
                        window[count++] = srcIdx[ny, nx];
                    }
                }
                Array.Sort(window);
                dstIdx[y, x] = window[midIndex];
            }
            var current = Interlocked.Increment(ref completed);
            if (current % 50 == 0) progress?.Report((double)current / rows * 100);
        });
    }

    private void RunRVSFCore<T>(Mat src, Mat dst, double A, double B, double N, IProgress<double> progress) where T : struct
    {
        int rows = src.Rows; int cols = src.Cols;
        double xNuc = cols / 2.0; double yNuc = rows / 2.0;
        var srcIdx = src.GetGenericIndexer<T>(); var dstIdx = dst.GetGenericIndexer<T>();
        int completed = 0;
        Parallel.For(0, rows, y => {
            double dySq = Math.Pow(y - yNuc, 2);
            for (int x = 0; x < cols; x++) {
                double rho = Math.Sqrt(Math.Pow(x - xNuc, 2) + dySq);
                int r = Math.Max(1, (int)Math.Round(A + (B * Math.Pow(rho, N))));
                int yMin = Math.Clamp(y - r, 0, rows - 1); int yMax = Math.Clamp(y + r, 0, rows - 1);
                int xMin = Math.Clamp(x - r, 0, cols - 1); int xMax = Math.Clamp(x + r, 0, cols - 1);
                double cur = Convert.ToDouble(srcIdx[y, x]);
                double sE = Convert.ToDouble(srcIdx[yMin, x]) + Convert.ToDouble(srcIdx[yMax, x]) + Convert.ToDouble(srcIdx[y, xMin]) + Convert.ToDouble(srcIdx[y, xMax]);
                double sC = Convert.ToDouble(srcIdx[yMin, xMin]) + Convert.ToDouble(srcIdx[yMin, xMax]) + Convert.ToDouble(srcIdx[yMax, xMin]) + Convert.ToDouble(srcIdx[yMax, xMax]);
                double res = (1024.0 * cur) - (192.0 * sE) - (64.0 * sC);
                dstIdx[y, x] = (T)Convert.ChangeType(res, typeof(T));
            }
            var current = Interlocked.Increment(ref completed);
            if (current % 100 == 0) progress?.Report((double)current / rows * 100);
        });
    }

    private Mat PrepareImageForRVSF(Mat src, bool useLog)
    {
        if (!useLog) return src.Clone();
        Mat work = new Mat();
        src.ConvertTo(work, src.Type());
        Cv2.Add(work, Scalar.All(1.0), work);
        Cv2.Log(work, work);
        work.ConvertTo(work, -1, 1.0 / Math.Log(10));
        return work;
    }
}