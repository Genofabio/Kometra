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
            Point2f center = new Point2f(src.Cols / 2f, src.Rows / 2f);

            Mat GetRotatedImage(Mat input, double deg, double sx, double sy)
            {
                using Mat rotMat = Cv2.GetRotationMatrix2D(center, deg, 1.0);
                var indexer = rotMat.GetGenericIndexer<double>();
                indexer[0, 2] += sx; indexer[1, 2] += sy;
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
    // PARTE 2: METODI DA RadialEnhancementEngine (Polar & InverseRho)
    // =========================================================

    public void ApplyInverseRho(Mat src, Mat dst, int subsampling = 5)
    {
        // Creiamo dst della stessa dimensione e tipo
        dst.Create(src.Size(), src.Type());

        // 1. Esecuzione Core (Calcolo matematico)
        if (src.Depth() == MatType.CV_64F) InverseRhoCore<double>(src, dst, subsampling);
        else InverseRhoCore<float>(src, dst, subsampling);

        // 2. CORREZIONE "EFFETTO TROPPO FORTE" (Gamma Compression)
        // L'Inverse Rho espande la dinamica in modo lineare, che spesso appare "esagerata".
        // Applicando una radice quadrata (Gamma ~2.2 inversa), riportiamo l'immagine a una
        // percezione naturale simile all'occhio umano.
    
        // Ignoriamo i NaN durante la Pow (OpenCV gestisce i NaN nella Pow lasciandoli NaN o 0)
        Cv2.Pow(dst, 0.5, dst); 
    
        // Opzionale: Normalizzazione finale per sicurezza (0.0 - 1.0)
        Cv2.Normalize(dst, dst, 0, 1, NormTypes.MinMax);
    }

    public Mat ToPolar(Mat src, int nRad, int nTheta, int subsampling = 5)
    {
        Point2f center = new Point2f(src.Cols / 2f, src.Rows / 2f);
        // WarpPolar supporta nativamente 32F e 64F
        Mat polarOpenCv = new Mat();
        Cv2.WarpPolar(src, polarOpenCv, new Size(nRad, nTheta), center, nRad, 
                      InterpolationFlags.Cubic | InterpolationFlags.WarpFillOutliers, 
                      WarpPolarMode.Linear);

        Mat polarCorrected = new Mat();
        Cv2.Rotate(polarOpenCv, polarCorrected, RotateFlags.Rotate90Clockwise);
        return polarCorrected;
    }

    public void FromPolar(Mat polar, Mat dst, int width, int height, int subsampling = 5)
    {
        dst.Create(new Size(width, height), polar.Type());

        using Mat polarForOpenCv = new Mat();
        Cv2.Rotate(polar, polarForOpenCv, RotateFlags.Rotate90Counterclockwise);

        Point2f center = new Point2f(width / 2f, height / 2f);
        double maxRadius = polar.Rows; 

        Cv2.WarpPolar(polarForOpenCv, dst, new Size(width, height), center, maxRadius,
                      InterpolationFlags.Cubic | InterpolationFlags.WarpInverseMap, 
                      WarpPolarMode.Linear);

        // FIX: Accesso al pixel centrale safe per float e double
        if (dst.Depth() == MatType.CV_64F)
        {
            var indexer = dst.GetGenericIndexer<double>();
            indexer[height / 2, width / 2] = 0.0;
        }
        else
        {
            var indexer = dst.GetGenericIndexer<float>();
            indexer[height / 2, width / 2] = 0f;
        }
    }

    public void ApplyAzimuthalAverage(Mat polar, double rejSig)
    {
        // FIX: Dispatching dinamico
        if (polar.Depth() == MatType.CV_64F) AzimuthalAverageCore<double>(polar, rejSig);
        else AzimuthalAverageCore<float>(polar, rejSig);
    }

    public void ApplyAzimuthalMedian(Mat polar)
    {
        // FIX: Dispatching dinamico
        if (polar.Depth() == MatType.CV_64F) AzimuthalMedianCore<double>(polar);
        else AzimuthalMedianCore<float>(polar);
    }

    public void ApplyAzimuthalRenormalization(Mat polar, double rejSig, double nSig)
    {
        // FIX: Dispatching dinamico
        if (polar.Depth() == MatType.CV_64F) AzimuthalRenormCore<double>(polar, rejSig, nSig);
        else AzimuthalRenormCore<float>(polar, rejSig, nSig);
    }

    // =========================================================
    // PRIVATE HELPERS (GENERIC)
    // =========================================================

    private void InverseRhoCore<T>(Mat src, Mat dst, int subsampling) where T : struct, IComparable<T>
{
    int rows = src.Rows; int cols = src.Cols;
    double xnuc = cols / 2.0; double ynuc = rows / 2.0;
    
    var srcIndexer = src.GetGenericIndexer<T>();
    var dstIndexer = dst.GetGenericIndexer<T>();

    // =========================================================
    // FASE 1: CALCOLO STATISTICHE ROBUSTO (Ignora NaN)
    // Non usiamo Cv2.MinMaxLoc perché fallisce se ci sono troppi NaN
    // =========================================================
    double minVal = double.MaxValue;
    double sumVal = 0;
    long validCount = 0;

    // Scansione seriale veloce per statistiche (O(N))
    // Nota: Potrebbe essere parallelizzato, ma per statistiche semplici è rapido anche così
    for (int y = 0; y < rows; y++) {
        for (int x = 0; x < cols; x++) {
            double val = Convert.ToDouble(srcIndexer[y, x]);
            if (!double.IsNaN(val)) {
                if (val < minVal) minVal = val;
                sumVal += val;
                validCount++;
            }
        }
    }

    // Se l'immagine è tutta NaN o vuota, usiamo default sicuri
    if (validCount == 0) {
        minVal = 0.0;
        sumVal = 1.0; // evita div/0 dopo
        validCount = 1;
    }

    double backgroundBias = minVal;
    double globalMean = (sumVal / validCount) - backgroundBias;
    // Fattore di rinormalizzazione (evita numeri giganti)
    double renormFactor = (globalMean > 1e-9) ? (100.0 / globalMean) : 1.0;

    // Pre-calcolo offset subsampling
    double step = 1.0 / subsampling;
    double offsetStart = -0.5 + (step / 2.0);
    double[] localOffsets = new double[subsampling];
    for (int i = 0; i < subsampling; i++) localOffsets[i] = offsetStart + (i * step);

    // =========================================================
    // FASE 2: APPLICAZIONE FILTRO
    // =========================================================
    Parallel.For(0, rows, y => {
        double baseDy = ynuc - y;
        for (int x = 0; x < cols; x++) {
            double rawVal = Convert.ToDouble(srcIndexer[y, x]);

            // Se è NaN, lo propaghiamo come NaN (o 0 se preferisci 'bucare' l'immagine)
            if (double.IsNaN(rawVal)) {
                // Impostiamo a NaN specificando il tipo corretto
                if (typeof(T) == typeof(double)) dstIndexer[y, x] = (T)(object)double.NaN;
                else dstIndexer[y, x] = (T)(object)float.NaN;
                continue;
            }

            // 1. Sottrazione Fondo Cielo (Cruciale per i bordi)
            // Usiamo Math.Max(0, ...) per evitare valori negativi che romperebbero la radice quadrata successiva
            double val = Math.Max(0, rawVal - backgroundBias);
            
            // 2. Normalizzazione luminosità
            double normalizedVal = val * renormFactor;

            // 3. Calcolo Distanza media (Inverse Rho)
            double sumRho = 0;
            double baseDx = xnuc - x;
            
            // Ciclo ottimizzato (loop unrolling parziale non necessario col JIT moderno, ma teniamo pulito)
            for (int jj = 0; jj < subsampling; jj++) {
                double dy = baseDy - localOffsets[jj];
                double dy2 = dy * dy;
                for (int ii = 0; ii < subsampling; ii++) {
                    double dx = baseDx - localOffsets[ii];
                    sumRho += Math.Sqrt(dx * dx + dy2);
                }
            }
            double avgDist = sumRho / (subsampling * subsampling);

            // 4. Clamp Distanza (Salva il centro nero)
            // Assumiamo che il raggio minimo utile sia ~20px per evitare moltiplicazioni quasi-zero
            // Se avgDist < 10, lo blocchiamo a 10.
            double multiplier = Math.Max(10.0, avgDist);

            // Risultato Lineare
            double result = normalizedVal * multiplier;
            
            dstIndexer[y, x] = (T)Convert.ChangeType(result, typeof(T));
        }
    });
}

    private void AzimuthalAverageCore<T>(Mat polar, double rejSig) where T : struct
    {
        int nRad = polar.Rows; int nTheta = polar.Cols;
        var indexer = polar.GetGenericIndexer<T>();

        Parallel.For(0, nRad, i => {
            int count = 0; double sum = 0, sumSq = 0;
            for (int j = 0; j < nTheta; j++) {
                double val = Convert.ToDouble(indexer[i, j]);
                if (!double.IsNaN(val) && val >= 0) { sum += val; sumSq += val * val; count++; }
            }
            if (count < 2) return;

            double mean1 = sum / count;
            double variance = (sumSq / count) - (mean1 * mean1);
            double sigma1 = variance > 0 ? Math.Sqrt(variance) : 0;
            double rejVal = rejSig * sigma1;
            double rejMin = Math.Max(1.0e-5, mean1 - rejVal);
            double rejMax = mean1 + rejVal;

            double cleanSum = 0; int cleanCount = 0;
            for (int j = 0; j < nTheta; j++) {
                double val = Convert.ToDouble(indexer[i, j]);
                if (!double.IsNaN(val) && val >= rejMin && val <= rejMax) { cleanSum += val; cleanCount++; }
            }
            double finalMean = cleanCount > 0 ? cleanSum / cleanCount : mean1;
            double divisor = finalMean + 1.0e-6;

            for (int j = 0; j < nTheta; j++) {
                double val = Convert.ToDouble(indexer[i, j]);
                if (!double.IsNaN(val)) 
                    indexer[i, j] = (T)Convert.ChangeType(val / divisor, typeof(T));
            }
        });
    }

    private void AzimuthalMedianCore<T>(Mat polar) where T : struct, IComparable<T>
    {
        int nRad = polar.Rows; int nTheta = polar.Cols;
        var indexer = polar.GetGenericIndexer<T>();

        Parallel.For(0, nRad, i => {
            // Buffer locale: usiamo double per il calcolo della mediana per semplicità
            double[] buffer = ArrayPool<double>.Shared.Rent(nTheta);
            int validCount = 0;
            try {
                for (int j = 0; j < nTheta; j++) {
                    double val = Convert.ToDouble(indexer[i, j]);
                    if (!double.IsNaN(val) && val >= 0) buffer[validCount++] = val;
                }
                if (validCount > 0) {
                    Array.Sort(buffer, 0, validCount);
                    double median = buffer[validCount / 2];
                    double divisor = median + 1e-6;
                    for (int j = 0; j < nTheta; j++) {
                        double val = Convert.ToDouble(indexer[i, j]);
                        if (!double.IsNaN(val))
                            indexer[i, j] = (T)Convert.ChangeType(val / divisor, typeof(T));
                    }
                }
            }
            finally { ArrayPool<double>.Shared.Return(buffer); }
        });
    }

    private void AzimuthalRenormCore<T>(Mat polar, double rejSig, double nSig) where T : struct
    {
        int nRad = polar.Rows; int nTheta = polar.Cols;
        var indexer = polar.GetGenericIndexer<T>();

        Parallel.For(0, nRad, i => {
            int count = 0; double sum = 0, sumSq = 0;
            for (int j = 0; j < nTheta; j++) {
                double val = Convert.ToDouble(indexer[i, j]);
                if (!double.IsNaN(val) && val >= 0) { sum += val; sumSq += val * val; count++; }
            }
            if (count < 3) return;

            double mean1 = sum / count;
            double variance = (sumSq / count) - (mean1 * mean1);
            double sigma1 = variance > 0 ? Math.Sqrt(variance) : 0;
            double rejVal = rejSig * sigma1;
            double rejMin = Math.Max(1.0e-5, mean1 - rejVal);
            double rejMax = mean1 + rejVal;
            
            double cleanSum = 0, cleanSumSq = 0; int cleanCount = 0;
            for (int j = 0; j < nTheta; j++) {
                double val = Convert.ToDouble(indexer[i, j]);
                if (!double.IsNaN(val) && val >= rejMin && val <= rejMax) { cleanSum += val; cleanSumSq += val*val; cleanCount++; }
            }
            if (cleanCount < 2) return;

            double finalMean = cleanSum / cleanCount;
            double finalVar = (cleanSumSq / cleanCount) - (finalMean * finalMean);
            double finalStd = finalVar > 0 ? Math.Sqrt(finalVar) : 0;
            double divisor = finalStd * nSig;
            if (divisor < 1e-9) divisor = 1.0;

            for (int j = 0; j < nTheta; j++) {
                double val = Convert.ToDouble(indexer[i, j]);
                if (!double.IsNaN(val))
                    indexer[i, j] = (T)Convert.ChangeType((val - finalMean) / divisor, typeof(T));
            }
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
            if (progress != null && current % 100 == 0) progress.Report((double)current / rows * 100);
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