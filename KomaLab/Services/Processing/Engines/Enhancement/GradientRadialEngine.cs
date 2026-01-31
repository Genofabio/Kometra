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
    // PARTE 1: METODI DA StructureExtractionEngine
    // =========================================================

    public async Task ApplyLarsonSekaninaAsync(Mat src, Mat dst, double angleDeg, double radialShiftX, double radialShiftY, bool isSymmetric)
    {
        await Task.Run(() =>
        {
            dst.Create(src.Size(), src.Type());
            Point2f center = new Point2f(src.Cols / 2.0f, src.Rows / 2.0f);

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
        try
        {
            await Task.Run(() =>
            {
                // FIX: Usa la logica originale (senza rinormalizzazione)
                using Mat workImg = PrepareImageForRVSF(src, useLog);
                dst.Create(src.Size(), workImg.Type());

                if (workImg.Depth() == MatType.CV_64F)
                    RunRVSFCoreDouble(workImg, dst, paramA, paramB, paramN, progress);
                else
                    RunRVSFCoreFloat(workImg, dst, paramA, paramB, paramN, progress);
            });
        }
        finally { _memSemaphore.Release(); }
    }

    public async Task ApplyRVSFMosaicAsync(Mat src, Mat dst, (double v1, double v2) paramA, (double v1, double v2) paramB, (double v1, double v2) paramN, bool useLog)
    {
        // FIX: Usa la logica originale (senza rinormalizzazione)
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
                try
                {
                    var (a, b, n) = combinations[index];
                    int gridRow = index / 4; int gridCol = index % 4;
                    using Mat subDst = new Mat(dst, new Rect(gridCol * w, gridRow * h, w, h));

                    if (workImg.Depth() == MatType.CV_64F)
                        RunRVSFCoreDouble(workImg, subDst, a, b, n, null);
                    else
                        RunRVSFCoreFloat(workImg, subDst, a, b, n, null);
                }
                finally { _memSemaphore.Release(); }
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
        if (src.Depth() == MatType.CV_64F) InverseRhoCoreDouble(src, dst, subsampling);
        else InverseRhoCoreFloat(src, dst, subsampling);
    }

    public Mat ToPolar(Mat src, int nRad, int nTheta, int subsampling = 10)
    {
        // TARGET: Righe=Raggio, Colonne=Angolo
        // WarpPolar genera: Righe=Angolo, Colonne=Raggio
        Point2f center = new Point2f(src.Cols / 2.0f, src.Rows / 2.0f);
        double maxRadius = nRad;

        using Mat cvPolar = new Mat();
        // WarpPolar con Lanczos4 e riempimento bordi
        Cv2.WarpPolar(
            src,
            cvPolar,
            new Size(nRad, nTheta),
            center,
            maxRadius,
            InterpolationFlags.Lanczos4 | InterpolationFlags.WarpFillOutliers,
            WarpPolarMode.Linear
        );

        // Trasposizione per matchare il formato (Raggio x Angolo)
        Mat dst = new Mat();
        Cv2.Transpose(cvPolar, dst);
        return dst;
    }

    public void FromPolar(Mat polar, Mat dst, int width, int height, int subsampling = 10)
    {
        // FIX RIGA BIANCA:
        // Usiamo Remap manuale con padding circolare sull'asse Angolo.
        // Input polar: [Righe=Raggio, Colonne=Angolo]
        
        // 1. Trasponiamo per avere (Righe=Angolo, Colonne=Raggio) che è lo standard per le mappe inverse
        using Mat polarTransposed = new Mat();
        Cv2.Transpose(polar, polarTransposed);

        // 2. Aggiungiamo padding circolare (Wrap) SOLO sulle righe (Angolo)
        // Questo permette all'interpolatore di leggere 360+delta prendendo da 0+delta.
        int pad = 16;
        using Mat polarPadded = new Mat();
        Cv2.CopyMakeBorder(polarTransposed, polarPadded, pad, pad, 0, 0, BorderTypes.Wrap);

        // 3. Prepariamo le mappe per Remap
        using Mat mapX = new Mat(height, width, MatType.CV_32FC1); // Mappa per Colonne (Raggio nel padded)
        using Mat mapY = new Mat(height, width, MatType.CV_32FC1); // Mappa per Righe (Angolo nel padded)

        double cx = width / 2.0f;
        double cy = height / 2.0f;
        double nTheta = polar.Cols; // Numero angoli originale
        
        // Otteniamo array per scrittura veloce
        mapX.GetArray(out float[] mx);
        mapY.GetArray(out float[] my);

        // 4. Calcoliamo le mappe
        Parallel.For(0, height, y =>
        {
            int rowOff = y * width;
            double dy = y - cy;
            for (int x = 0; x < width; x++)
            {
                double dx = x - cx;
                
                // Raggio (corrisponde alle Colonne in polarPadded)
                double rho = Math.Sqrt(dx * dx + dy * dy);
                
                // Angolo (corrisponde alle Righe in polarPadded)
                double theta = Math.Atan2(dy, dx);
                if (theta < 0) theta += 2 * Math.PI; // Normalizza 0..2PI
                
                // Indice angolo nell'immagine originale
                double angleIdx = (theta / (2 * Math.PI)) * nTheta;

                // ASSEGNAZIONE MAPPE (Attenzione qui!)
                // mapX controlla la Colonna (Raggio)
                mx[rowOff + x] = (float)rho;

                // mapY controlla la Riga (Angolo)
                // Aggiungiamo 'pad' perché l'immagine sorgente è shiftata in basso di 'pad' pixel
                my[rowOff + x] = (float)(angleIdx + pad);
            }
        });

        // 5. IMPORTANTE: Salviamo i dati calcolati nelle Mat (Fix per immagine nera)
        mapX.SetArray(mx);
        mapY.SetArray(my);

        // 6. Eseguiamo Remap
        // Lanczos4 userà i dati nel padding per interpolare correttamente a 0/360 gradi
        Cv2.Remap(polarPadded, dst, mapX, mapY, InterpolationFlags.Lanczos4, BorderTypes.Constant, Scalar.All(0));
    }

    public void ApplyAzimuthalAverage(Mat polar, double rejSig)
    {
        if (polar.Depth() == MatType.CV_64F) AzimuthalAverageCoreDouble(polar, rejSig);
        else AzimuthalAverageCoreFloat(polar, rejSig);
    }

    public void ApplyAzimuthalMedian(Mat polar)
    {
        if (polar.Depth() == MatType.CV_64F) AzimuthalMedianCoreDouble(polar);
        else AzimuthalMedianCoreFloat(polar);
    }

    public void ApplyAzimuthalRenormalization(Mat polar, double rejSig, double nSig)
    {
        if (polar.Depth() == MatType.CV_64F) AzimuthalRenormCoreDouble(polar, rejSig, nSig);
        else AzimuthalRenormCoreFloat(polar, rejSig, nSig);
    }

    // =========================================================
    // PRIVATE CORE IMPLEMENTATIONS (OPTIMIZED WITH MANAGED ARRAYS)
    // =========================================================

    // --- RVSF CORE DOUBLE ---
    private void RunRVSFCoreDouble(Mat src, Mat dst, double A, double B, double N, IProgress<double> progress)
    {
        int rows = src.Rows; int cols = src.Cols;
        double xNuc = cols / 2.0;
        double yNuc = rows / 2.0;

        src.GetArray(out double[] sArr);
        double[] dArr = new double[sArr.Length];

        int completed = 0;

        Parallel.For(0, rows, (int y) => {
            int rowOffset = y * cols;
            double dy = y - yNuc;
            double dySq = dy * dy;

            for (int x = 0; x < cols; x++)
            {
                // 1. Calcolo del raggio UNICO per pixel (Logica originale)
                double dx = x - xNuc;
                double rho = Math.Sqrt(dx * dx + dySq);
                
                int r = (int)Math.Round(A + (B * Math.Pow(rho, N)));
                if (r < 1) r = 1;

                // 2. Clamping degli indici (Border Replicate) - Essenziale per i bordi
                int yMin = (y - r < 0) ? 0 : (y - r >= rows ? rows - 1 : y - r);
                int yMax = (y + r < 0) ? 0 : (y + r >= rows ? rows - 1 : y + r);
                int xMin = (x - r < 0) ? 0 : (x - r >= cols ? cols - 1 : x - r);
                int xMax = (x + r < 0) ? 0 : (x + r >= cols ? cols - 1 : x + r);

                // 3. Somme pesate usando gli indici clampati
                double valC = sArr[rowOffset + x];

                double sE = sArr[yMin * cols + x] + 
                            sArr[yMax * cols + x] + 
                            sArr[y * cols + xMin] + 
                            sArr[y * cols + xMax];

                double sC = sArr[yMin * cols + xMin] + 
                            sArr[yMin * cols + xMax] + 
                            sArr[yMax * cols + xMin] + 
                            sArr[yMax * cols + xMax];

                // 4. Formula RVSF Originale
                double res = (1024.0 * valC) - (192.0 * sE) - (64.0 * sC);
                dArr[rowOffset + x] = res;
            }
            if (progress != null)
            {
                var current = Interlocked.Increment(ref completed);
                if (current % 100 == 0) progress.Report((double)current / rows * 100);
            }
        });
        dst.SetArray(dArr);
    }

    // --- RVSF CORE FLOAT (FIXED) ---
    private void RunRVSFCoreFloat(Mat src, Mat dst, double A, double B, double N, IProgress<double> progress)
    {
        int rows = src.Rows; int cols = src.Cols;
        double xNuc = cols / 2.0;
        double yNuc = rows / 2.0;

        src.GetArray(out float[] sArr);
        float[] dArr = new float[sArr.Length];

        int completed = 0;

        Parallel.For(0, rows, (int y) => {
            int rowOffset = y * cols;
            double dy = y - yNuc;
            double dySq = dy * dy;

            for (int x = 0; x < cols; x++)
            {
                double dx = x - xNuc;
                double rho = Math.Sqrt(dx * dx + dySq);
                
                int r = (int)Math.Round(A + (B * Math.Pow(rho, N)));
                if (r < 1) r = 1;

                int yMin = (y - r < 0) ? 0 : (y - r >= rows ? rows - 1 : y - r);
                int yMax = (y + r < 0) ? 0 : (y + r >= rows ? rows - 1 : y + r);
                int xMin = (x - r < 0) ? 0 : (x - r >= cols ? cols - 1 : x - r);
                int xMax = (x + r < 0) ? 0 : (x + r >= cols ? cols - 1 : x + r);

                float valC = sArr[rowOffset + x];

                float sE = sArr[yMin * cols + x] + 
                           sArr[yMax * cols + x] + 
                           sArr[y * cols + xMin] + 
                           sArr[y * cols + xMax];

                float sC = sArr[yMin * cols + xMin] + 
                           sArr[yMin * cols + xMax] + 
                           sArr[yMax * cols + xMin] + 
                           sArr[yMax * cols + xMax];

                double res = (1024.0 * valC) - (192.0 * sE) - (64.0 * sC);
                dArr[rowOffset + x] = (float)res;
            }
            if (progress != null)
            {
                var current = Interlocked.Increment(ref completed);
                if (current % 100 == 0) progress.Report((double)current / rows * 100);
            }
        });
        dst.SetArray(dArr);
    }
    

    // --- INVERSE RHO DOUBLE ---
    private void InverseRhoCoreDouble(Mat src, Mat dst, int subsampling)
    {
        int rows = src.Rows; int cols = src.Cols;
        double xNuc = cols / 2.0; double yNuc = rows / 2.0;

        src.GetArray(out double[] sArr);
        double[] dArr = new double[sArr.Length];

        Scalar mean = Cv2.Mean(src);
        double renorm = 100.0 / (Math.Abs(mean.Val0) > 1e-9 ? mean.Val0 : 1.0);

        Parallel.For(0, rows, (int y) => {
            int rowOffset = y * cols;
            for (int x = 0; x < cols; x++)
            {
                double rhoSum = 0;
                for (int m = 0; m < 10; m++)
                {
                    double dy = (y + 0.5 - yNuc) - 0.45 + (m * 0.1);
                    for (int n = 0; n < 10; n++)
                    {
                        double dx = (x + 0.5 - xNuc) - 0.45 + (n * 0.1);
                        rhoSum += Math.Sqrt(dx * dx + dy * dy);
                    }
                }
                double val = sArr[rowOffset + x] * renorm;
                dArr[rowOffset + x] = val * (rhoSum * 0.01);
            }
        });
        dst.SetArray(dArr);
    }

    // --- INVERSE RHO FLOAT ---
    private void InverseRhoCoreFloat(Mat src, Mat dst, int subsampling)
    {
        int rows = src.Rows; int cols = src.Cols;
        double xNuc = cols / 2.0; double yNuc = rows / 2.0;

        src.GetArray(out float[] sArr);
        float[] dArr = new float[sArr.Length];

        Scalar mean = Cv2.Mean(src);
        double renorm = 100.0 / (Math.Abs(mean.Val0) > 1e-9 ? mean.Val0 : 1.0);

        Parallel.For(0, rows, (int y) => {
            int rowOffset = y * cols;
            for (int x = 0; x < cols; x++)
            {
                double rhoSum = 0;
                for (int m = 0; m < 10; m++)
                {
                    double dy = (y + 0.5 - yNuc) - 0.45 + (m * 0.1);
                    for (int n = 0; n < 10; n++)
                    {
                        double dx = (x + 0.5 - xNuc) - 0.45 + (n * 0.1);
                        rhoSum += Math.Sqrt(dx * dx + dy * dy);
                    }
                }
                double val = sArr[rowOffset + x] * renorm;
                dArr[rowOffset + x] = (float)(val * (rhoSum * 0.01));
            }
        });
        dst.SetArray(dArr);
    }

    // --- AZIMUTHAL METHODS DOUBLE ---
    private void AzimuthalAverageCoreDouble(Mat polar, double rejSig)
    {
        int nRad = polar.Rows; int nTheta = polar.Cols;
        polar.GetArray(out double[] arr);

        Parallel.For(0, nRad, (int i) => {
            int rowOffset = i * nTheta;
            double sum = 0, sumSq = 0; int count = 0;
            for (int j = 0; j < nTheta; j++)
            {
                double v = arr[rowOffset + j];
                if (!double.IsNaN(v) && v >= 0) { sum += v; sumSq += v * v; count++; }
            }
            if (count < 2) return;
            double m1 = sum / count;
            double s1 = Math.Sqrt(Math.Max(0, (sumSq / count) - (m1 * m1)));
            double rMin = Math.Max(0, m1 - rejSig * s1), rMax = m1 + rejSig * s1;

            double cSum = 0; int cCount = 0;
            for (int j = 0; j < nTheta; j++)
            {
                double v = arr[rowOffset + j];
                if (v >= rMin && v <= rMax) { cSum += v; cCount++; }
            }
            double finalMean = cCount > 0 ? cSum / cCount : m1;
            double div = finalMean + 1e-9;
            for (int j = 0; j < nTheta; j++)
                arr[rowOffset + j] /= div;
        });
        polar.SetArray(arr);
    }

    private void AzimuthalMedianCoreDouble(Mat polar)
    {
        int nRad = polar.Rows; int nTheta = polar.Cols;
        polar.GetArray(out double[] arr);

        Parallel.For(0, nRad, (int i) => {
            int rowOffset = i * nTheta;
            double[] buffer = ArrayPool<double>.Shared.Rent(nTheta);
            int vc = 0;
            try
            {
                for (int j = 0; j < nTheta; j++)
                {
                    double v = arr[rowOffset + j];
                    if (!double.IsNaN(v)) buffer[vc++] = v;
                }
                if (vc > 0)
                {
                    Array.Sort(buffer, 0, vc);
                    double median = buffer[vc / 2];
                    double div = median + 1e-9;
                    for (int j = 0; j < nTheta; j++)
                        arr[rowOffset + j] /= div;
                }
            }
            finally { ArrayPool<double>.Shared.Return(buffer); }
        });
        polar.SetArray(arr);
    }

    private void AzimuthalRenormCoreDouble(Mat polar, double rejSig, double nSig)
    {
        int nRad = polar.Rows; int nTheta = polar.Cols;
        polar.GetArray(out double[] arr);

        Parallel.For(0, nRad, (int i) => {
            int rowOffset = i * nTheta;
            double sum = 0, sumSq = 0; int count = 0;
            for (int j = 0; j < nTheta; j++)
            {
                double v = arr[rowOffset + j];
                if (v >= 0) { sum += v; sumSq += v * v; count++; }
            }
            if (count < 3) return;
            double m1 = sum / count;
            double s1 = Math.Sqrt(Math.Max(0, (sumSq / count) - (m1 * m1)));
            double rMin = m1 - rejSig * s1, rMax = m1 + rejSig * s1;

            double cSum = 0, cSumSq = 0; int cc = 0;
            for (int j = 0; j < nTheta; j++)
            {
                double v = arr[rowOffset + j];
                if (v >= rMin && v <= rMax) { cSum += v; cSumSq += v * v; cc++; }
            }
            if (cc < 2) return;
            double fm = cSum / cc;
            double fs = Math.Sqrt(Math.Max(0, (cSumSq / cc) - (fm * fm)));
            double rowMin = fm - nSig * fs, rowMax = fm + nSig * fs;
            double diff = Math.Max(1e-9, rowMax - rowMin);

            for (int j = 0; j < nTheta; j++)
                arr[rowOffset + j] = (arr[rowOffset + j] - rowMin) / diff;
        });
        polar.SetArray(arr);
    }

    // --- AZIMUTHAL METHODS FLOAT ---
    private void AzimuthalAverageCoreFloat(Mat polar, double rejSig)
    {
        int nRad = polar.Rows; int nTheta = polar.Cols;
        polar.GetArray(out float[] arr);

        Parallel.For(0, nRad, (int i) => {
            int rowOffset = i * nTheta;
            double sum = 0, sumSq = 0; int count = 0;
            for (int j = 0; j < nTheta; j++)
            {
                double v = arr[rowOffset + j];
                if (!double.IsNaN(v) && v >= 0) { sum += v; sumSq += v * v; count++; }
            }
            if (count < 2) return;
            double m1 = sum / count;
            double s1 = Math.Sqrt(Math.Max(0, (sumSq / count) - (m1 * m1)));
            double rMin = Math.Max(0, m1 - rejSig * s1), rMax = m1 + rejSig * s1;

            double cSum = 0; int cCount = 0;
            for (int j = 0; j < nTheta; j++)
            {
                double v = arr[rowOffset + j];
                if (v >= rMin && v <= rMax) { cSum += v; cCount++; }
            }
            double finalMean = cCount > 0 ? cSum / cCount : m1;
            double div = finalMean + 1e-9;
            for (int j = 0; j < nTheta; j++)
                arr[rowOffset + j] = (float)(arr[rowOffset + j] / div);
        });
        polar.SetArray(arr);
    }

    private void AzimuthalMedianCoreFloat(Mat polar)
    {
        int nRad = polar.Rows; int nTheta = polar.Cols;
        polar.GetArray(out float[] arr);

        Parallel.For(0, nRad, (int i) => {
            int rowOffset = i * nTheta;
            double[] buffer = ArrayPool<double>.Shared.Rent(nTheta);
            int vc = 0;
            try
            {
                for (int j = 0; j < nTheta; j++)
                {
                    double v = arr[rowOffset + j];
                    if (!double.IsNaN(v)) buffer[vc++] = v;
                }
                if (vc > 0)
                {
                    Array.Sort(buffer, 0, vc);
                    double median = buffer[vc / 2];
                    double div = median + 1e-9;
                    for (int j = 0; j < nTheta; j++)
                        arr[rowOffset + j] = (float)(arr[rowOffset + j] / div);
                }
            }
            finally { ArrayPool<double>.Shared.Return(buffer); }
        });
        polar.SetArray(arr);
    }

    private void AzimuthalRenormCoreFloat(Mat polar, double rejSig, double nSig)
    {
        int nRad = polar.Rows; int nTheta = polar.Cols;
        polar.GetArray(out float[] arr);

        Parallel.For(0, nRad, (int i) => {
            int rowOffset = i * nTheta;
            double sum = 0, sumSq = 0; int count = 0;
            for (int j = 0; j < nTheta; j++)
            {
                double v = arr[rowOffset + j];
                if (v >= 0) { sum += v; sumSq += v * v; count++; }
            }
            if (count < 3) return;
            double m1 = sum / count;
            double s1 = Math.Sqrt(Math.Max(0, (sumSq / count) - (m1 * m1)));
            double rMin = m1 - rejSig * s1, rMax = m1 + rejSig * s1;

            double cSum = 0, cSumSq = 0; int cc = 0;
            for (int j = 0; j < nTheta; j++)
            {
                double v = arr[rowOffset + j];
                if (v >= rMin && v <= rMax) { cSum += v; cSumSq += v * v; cc++; }
            }
            if (cc < 2) return;
            double fm = cSum / cc;
            double fs = Math.Sqrt(Math.Max(0, (cSumSq / cc) - (fm * fm)));
            double rowMin = fm - nSig * fs, rowMax = fm + nSig * fs;
            double diff = Math.Max(1e-9, rowMax - rowMin);

            for (int j = 0; j < nTheta; j++)
                arr[rowOffset + j] = (float)((arr[rowOffset + j] - rowMin) / diff);
        });
        polar.SetArray(arr);
    }

    private Mat PrepareImageForRVSF(Mat src, bool useLog)
    {
        // FIX: Rimosso calcolo della media e rinormalizzazione (100.0/mean).
        // RVSF lavora sui valori locali, normalizzare globalmente altera l'output.
        
        Mat work = new Mat();
        if (!useLog)
        {
            src.ConvertTo(work, MatType.CV_64F);
        }
        else
        {
            src.ConvertTo(work, MatType.CV_64F);
            // Logica Logaritmica Originale: Log(val + 1)
            Cv2.Add(work, Scalar.All(1.0), work);
            Cv2.Log(work, work);
            // Conversione base 10
            work.ConvertTo(work, -1, 1.0 / Math.Log(10));
        }
        return work;
    }
}