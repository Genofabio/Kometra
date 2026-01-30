using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;

namespace KomaLab.Services.Processing.Engines.Enhancement;

public class GradientRadialEngine : IGradientRadialEngine
{
    private readonly SemaphoreSlim _memSemaphore = new SemaphoreSlim(Math.Max(1, Environment.ProcessorCount / 2));

    // =========================================================
    // PARTE 1: METODI DA StructureExtractionEngine (Larson & RVSF)
    // =========================================================

    public async Task ApplyLarsonSekaninaAsync(Mat src, Mat dst, double angleDeg, double radialShiftX, double radialShiftY, bool isSymmetric)
    {
        await Task.Run(() =>
        {
            dst.Create(src.Size(), src.Type());
            // Il centro geometrico per la rotazione OpenCV 
            // In un'immagine 801x801, 400.5 è l'intersezione esatta tra i pixel centrali.
            Point2f center = new Point2f(src.Cols / 2.0f, src.Rows / 2.0f);

            Mat GetRotatedImage(Mat input, double deg, double sx, double sy)
            {
                using Mat rotMat = Cv2.GetRotationMatrix2D(center, deg, 1.0);
                var indexer = rotMat.GetGenericIndexer<double>();
                indexer[0, 2] += sx; 
                indexer[1, 2] += sy;
                
                Mat res = new Mat();
                // Interpolazione cubica per precisione sub-pixel nelle strutture fini
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
    // PARTE 2: METODI RADIALI (POLARI)
    // =========================================================

    public void ApplyInverseRho(Mat src, Mat dst, int subsampling = 10)
    {
        dst.Create(src.Size(), src.Type());
        if (src.Depth() == MatType.CV_64F) InverseRhoCore<double>(src, dst, subsampling);
        else InverseRhoCore<float>(src, dst, subsampling);
    }

    public Mat ToPolar(Mat src, int nRad, int nTheta, int subsampling = 10)
    {
        Mat dst = new Mat(new Size(nTheta, nRad), src.Type());
        if (src.Depth() == MatType.CV_64F) ToPolarCore<double>(src, dst, nRad, nTheta, subsampling);
        else ToPolarCore<float>(src, dst, nRad, nTheta, subsampling);
        return dst;
    }

    public void FromPolar(Mat polar, Mat dst, int width, int height, int subsampling = 10)
    {
        dst.Create(new Size(width, height), polar.Type());
        if (polar.Depth() == MatType.CV_64F) FromPolarCore<double>(polar, dst, width, height, subsampling);
        else FromPolarCore<float>(polar, dst, width, height, subsampling);
    }

    public void ApplyAzimuthalAverage(Mat polar, double rejSig)
    {
        if (polar.Depth() == MatType.CV_64F) AzimuthalAverageCore<double>(polar, rejSig);
        else AzimuthalAverageCore<float>(polar, rejSig);
    }

    public void ApplyAzimuthalMedian(Mat polar)
    {
        if (polar.Depth() == MatType.CV_64F) AzimuthalMedianCore<double>(polar);
        else AzimuthalMedianCore<float>(polar);
    }

    public void ApplyAzimuthalRenormalization(Mat polar, double rejSig, double nSig)
    {
        if (polar.Depth() == MatType.CV_64F) AzimuthalRenormCore<double>(polar, rejSig, nSig);
        else AzimuthalRenormCore<float>(polar, rejSig, nSig);
    }

    // =========================================================
    // PRIVATE CORE IMPLEMENTATIONS
    // =========================================================

    private void RunRVSFCore<T>(Mat src, Mat dst, double A, double B, double N, IProgress<double> progress) where T : struct
    {
        int rows = src.Rows; int cols = src.Cols;
        double xNuc = cols / 2.0; 
        double yNuc = rows / 2.0;
        
        var srcIdx = src.GetGenericIndexer<T>();
        var dstIdx = dst.GetGenericIndexer<T>();
        
        int completed = 0;
        double[] offsets = new double[10];
        for (int k = 1; k <= 10; k++) offsets[k - 1] = -0.45 + (k - 1) * 0.1;

        Parallel.For(0, rows, (int y) => {
            for (int x = 0; x < cols; x++) {
                double sumEdg = 0.0, sumCrn = 0.0;
                for (int m = 0; m < 10; m++) {
                    double subY = (y + 0.5 - yNuc) + offsets[m]; 
                    for (int n = 0; n < 10; n++) {
                        double subX = (x + 0.5 - xNuc) + offsets[n]; 
                        double rho = Math.Sqrt(subX * subX + subY * subY);
                        double radius = A + (B * Math.Pow(rho, N));
                        int rInt = (int)Math.Round(radius); 

                        int[] dy = { -rInt, rInt, 0, 0, -rInt, -rInt, rInt, rInt };
                        int[] dx = { 0, 0, -rInt, rInt, -rInt, rInt, -rInt, rInt };
                        
                        for (int k = 0; k < 4; k++) { 
                            int yi = y + dy[k], xi = x + dx[k];
                            if (yi >= 0 && yi < rows && xi >= 0 && xi < cols) sumEdg += Convert.ToDouble(srcIdx[yi, xi]);
                        }
                        for (int k = 4; k < 8; k++) { 
                            int yi = y + dy[k], xi = x + dx[k];
                            if (yi >= 0 && yi < rows && xi >= 0 && xi < cols) sumCrn += Convert.ToDouble(srcIdx[yi, xi]);
                        }
                    }
                }
                double res = (1024.0 * Convert.ToDouble(srcIdx[y, x])) - (1.92 * sumEdg) - (0.64 * sumCrn);
                dstIdx[y, x] = (T)Convert.ChangeType(res, typeof(T));
            }
            if (progress != null) {
                var current = Interlocked.Increment(ref completed);
                if (current % 100 == 0) progress.Report((double)current / rows * 100);
            }
        });
    }

    private void InverseRhoCore<T>(Mat src, Mat dst, int subsampling) where T : struct
    {
        int rows = src.Rows; int cols = src.Cols;
        double xNuc = cols / 2.0; double yNuc = rows / 2.0;
        var srcIdx = src.GetGenericIndexer<T>();
        var dstIdx = dst.GetGenericIndexer<T>();

        Scalar mean = Cv2.Mean(src);
        double renorm = 100.0 / (Math.Abs(mean.Val0) > 1e-9 ? mean.Val0 : 1.0);
        
        Parallel.For(0, rows, (int y) => {
            for (int x = 0; x < cols; x++) {
                double rhoSum = 0;
                for (int m = 0; m < 10; m++) {
                    double dy = (y + 0.5 - yNuc) - 0.45 + (m * 0.1);
                    for (int n = 0; n < 10; n++) {
                        double dx = (x + 0.5 - xNuc) - 0.45 + (n * 0.1);
                        rhoSum += Math.Sqrt(dx * dx + dy * dy);
                    }
                }
                double val = Convert.ToDouble(srcIdx[y, x]) * renorm;
                dstIdx[y, x] = (T)Convert.ChangeType(val * (rhoSum * 0.01), typeof(T));
            }
        });
    }

    private void ToPolarCore<T>(Mat src, Mat dst, int nRad, int nTheta, int subsampling) where T : struct
    {
        int sw = src.Cols; int sh = src.Rows;
        double xNuc = sw / 2.0; double yNuc = sh / 2.0;
        double twoPi = 2.0 * Math.PI;
        var srcIdx = src.GetGenericIndexer<T>();
        var dstIdx = dst.GetGenericIndexer<T>();

        Parallel.For(0, nRad, (int r) => {
            double radiusBase = r + 1.0; 
            for (int t = 0; t < nTheta; t++) {
                double avect_t = 0;
                for (int ii = 0; ii < 10; ii++) {
                    double ai = radiusBase - 0.45 + (ii * 0.1);
                    for (int jj = 0; jj < 10; jj++) {
                        double angle = ((t * 10.0 + jj) + 0.5) * twoPi / (nTheta * 10.0);
                        // Calcolo coordinate sub-pixel (0.5 è il centro del pixel indice 0)
                        double xPos = xNuc + (ai * -Math.Sin(angle));
                        double yPos = yNuc + (ai * Math.Cos(angle));
                        
                        // Passaggio all'interpolatore bilineare (convertiamo in 0-based puro sottraendo 0.5)
                        avect_t += GetBilinearSample(srcIdx, xPos - 0.5, yPos - 0.5, sw, sh);
                    }
                }
                dstIdx[r, t] = (T)Convert.ChangeType(avect_t * 0.01, typeof(T));
            }
        });
    }

    private void FromPolarCore<T>(Mat polar, Mat cartes, int w, int h, int subsampling) where T : struct
    {
        int nRad = polar.Rows; int nTheta = polar.Cols;
        double xNuc = w / 2.0; double yNuc = h / 2.0;
        double twoPi = 2.0 * Math.PI;
        var polIdx = polar.GetGenericIndexer<T>();
        var carIdx = cartes.GetGenericIndexer<T>();

        Parallel.For(0, h, (int i) => {
            for (int j = 0; j < w; j++) {
                double cvect_j = 0;
                for (int ii = 0; ii < 10; ii++) {
                    double dy = (i + 0.5 - yNuc) - 0.45 + (ii * 0.1);
                    for (int jj = 0; jj < 10; jj++) {
                        double dx = (j + 0.5 - xNuc) - 0.45 + (jj * 0.1);
                        double rho = Math.Sqrt(dx * dx + dy * dy);
                        
                        if (rho >= 0.5 && rho <= nRad + 0.5) {
                            double ang = Math.Atan2(-dx, dy);
                            if (ang < 0) ang += twoPi;
                            
                            double rCoord = rho - 1.0;
                            double tCoord = ang * nTheta / twoPi;
                            
                            cvect_j += GetBilinearSample(polIdx, tCoord, rCoord, nTheta, nRad);
                        }
                    }
                }
                carIdx[i, j] = (T)Convert.ChangeType(cvect_j * 0.01, typeof(T));
            }
        });
    }

    private double GetBilinearSample<T>(MatIndexer<T> indexer, double x, double y, int w, int h) where T : struct
    {
        // Out of bounds check
        if (x < 0 || x > w - 1 || y < 0 || y > h - 1) return 0.0;

        int x1 = (int)Math.Floor(x);
        int y1 = (int)Math.Floor(y);
        int x2 = Math.Min(x1 + 1, w - 1);
        int y2 = Math.Min(y1 + 1, h - 1);

        double dx = x - x1;
        double dy = y - y1;

        double v1 = Convert.ToDouble(indexer[y1, x1]);
        double v2 = Convert.ToDouble(indexer[y1, x2]);
        double v3 = Convert.ToDouble(indexer[y2, x1]);
        double v4 = Convert.ToDouble(indexer[y2, x2]);

        return v1 * (1 - dx) * (1 - dy) + v2 * dx * (1 - dy) + 
               v3 * (1 - dx) * dy + v4 * dx * dy;
    }

    private void AzimuthalAverageCore<T>(Mat polar, double rejSig) where T : struct
    {
        int nRad = polar.Rows; int nTheta = polar.Cols;
        var indexer = polar.GetGenericIndexer<T>();
        Parallel.For(0, nRad, (int i) => {
            double sum = 0, sumSq = 0; int count = 0;
            for (int j = 0; j < nTheta; j++) {
                double v = Convert.ToDouble(indexer[i, j]);
                if (!double.IsNaN(v) && v >= 0) { sum += v; sumSq += v * v; count++; }
            }
            if (count < 2) return;
            double m1 = sum / count;
            double s1 = Math.Sqrt(Math.Max(0, (sumSq / count) - (m1 * m1)));
            double rMin = Math.Max(0, m1 - rejSig * s1), rMax = m1 + rejSig * s1;
            
            double cSum = 0; int cCount = 0;
            for (int j = 0; j < nTheta; j++) {
                double v = Convert.ToDouble(indexer[i, j]);
                if (v >= rMin && v <= rMax) { cSum += v; cCount++; }
            }
            double finalMean = cCount > 0 ? cSum / cCount : m1;
            for (int j = 0; j < nTheta; j++) 
                indexer[i, j] = (T)Convert.ChangeType(Convert.ToDouble(indexer[i, j]) / (finalMean + 1e-9), typeof(T));
        });
    }

    private void AzimuthalMedianCore<T>(Mat polar) where T : struct, IComparable<T>
    {
        int nRad = polar.Rows; int nTheta = polar.Cols;
        var indexer = polar.GetGenericIndexer<T>();
        Parallel.For(0, nRad, (int i) => {
            double[] buffer = ArrayPool<double>.Shared.Rent(nTheta);
            int vc = 0;
            try {
                for (int j = 0; j < nTheta; j++) {
                    double v = Convert.ToDouble(indexer[i, j]);
                    if (!double.IsNaN(v)) buffer[vc++] = v;
                }
                if (vc > 0) {
                    Array.Sort(buffer, 0, vc);
                    double median = buffer[vc / 2];
                    for (int j = 0; j < nTheta; j++) 
                        indexer[i, j] = (T)Convert.ChangeType(Convert.ToDouble(indexer[i, j]) / (median + 1e-9), typeof(T));
                }
            } finally { ArrayPool<double>.Shared.Return(buffer); }
        });
    }

    private void AzimuthalRenormCore<T>(Mat polar, double rejSig, double nSig) where T : struct
    {
        int nRad = polar.Rows; int nTheta = polar.Cols;
        var indexer = polar.GetGenericIndexer<T>();
        Parallel.For(0, nRad, (int i) => {
            double sum = 0, sumSq = 0; int count = 0;
            for (int j = 0; j < nTheta; j++) {
                double v = Convert.ToDouble(indexer[i, j]);
                if (v >= 0) { sum += v; sumSq += v * v; count++; }
            }
            if (count < 3) return;
            double m1 = sum / count;
            double s1 = Math.Sqrt(Math.Max(0, (sumSq / count) - (m1 * m1)));
            double rMin = m1 - rejSig * s1, rMax = m1 + rejSig * s1;
            
            double cSum = 0, cSumSq = 0; int cc = 0;
            for (int j = 0; j < nTheta; j++) {
                double v = Convert.ToDouble(indexer[i, j]);
                if (v >= rMin && v <= rMax) { cSum += v; cSumSq += v * v; cc++; }
            }
            if (cc < 2) return;
            double fm = cSum / cc;
            double fs = Math.Sqrt(Math.Max(0, (cSumSq / cc) - (fm * fm)));
            double rowMin = fm - nSig * fs, rowMax = fm + nSig * fs;
            double diff = Math.Max(1e-9, rowMax - rowMin);
            
            for (int j = 0; j < nTheta; j++) 
                indexer[i, j] = (T)Convert.ChangeType((Convert.ToDouble(indexer[i, j]) - rowMin) / diff, typeof(T));
        });
    }

    private Mat PrepareImageForRVSF(Mat src, bool useLog)
    {
        Mat work = new Mat();
        Scalar mean = Cv2.Mean(src);
        double renorm = (Math.Abs(mean.Val0) > 1e-9) ? 100.0 / mean.Val0 : 1.0;
        src.ConvertTo(work, MatType.CV_64F, renorm);
        
        if (useLog) { 
            Cv2.Max(work, 1e-12, work); 
            Cv2.Log(work, work); 
            work.ConvertTo(work, -1, 1.0 / Math.Log(10)); 
        }
        return work;
    }
}