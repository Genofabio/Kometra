using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;

namespace KomaLab.Services.Processing.Engines.Enhancement;

public class GradientRadialEngine : IGradientRadialEngine
{
    private readonly SemaphoreSlim _memSemaphore = new SemaphoreSlim(Math.Max(1, Environment.ProcessorCount / 2));

    // =========================================================
    // PARTE 1: METODI STANDARD (Larson-Sekanina, RVSF)
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
        Point2f center = new Point2f(src.Cols / 2.0f, src.Rows / 2.0f);
        double maxRadius = nRad;

        using Mat cvPolar = new Mat();
        Cv2.WarpPolar(
            src,
            cvPolar,
            new Size(nRad, nTheta),
            center,
            maxRadius,
            InterpolationFlags.Lanczos4 | InterpolationFlags.WarpFillOutliers,
            WarpPolarMode.Linear
        );

        Mat dst = new Mat();
        Cv2.Transpose(cvPolar, dst);
        return dst;
    }

    public void FromPolar(Mat polar, Mat dst, int width, int height, int subsampling = 10)
    {
        using Mat polarTransposed = new Mat();
        Cv2.Transpose(polar, polarTransposed);

        int pad = 16;
        using Mat polarPadded = new Mat();
        Cv2.CopyMakeBorder(polarTransposed, polarPadded, pad, pad, 0, 0, BorderTypes.Wrap);

        using Mat mapX = new Mat(height, width, MatType.CV_32FC1); 
        using Mat mapY = new Mat(height, width, MatType.CV_32FC1); 

        double cx = width / 2.0f;
        double cy = height / 2.0f;
        double nTheta = polar.Cols; 
        
        mapX.GetArray(out float[] mx);
        mapY.GetArray(out float[] my);

        Parallel.For(0, height, y =>
        {
            int rowOff = y * width;
            double dy = y - cy;
            for (int x = 0; x < width; x++)
            {
                double dx = x - cx;
                double rho = Math.Sqrt(dx * dx + dy * dy);
                double theta = Math.Atan2(dy, dx);
                if (theta < 0) theta += 2 * Math.PI; 
                double angleIdx = (theta / (2 * Math.PI)) * nTheta;

                mx[rowOff + x] = (float)rho;
                my[rowOff + x] = (float)(angleIdx + pad);
            }
        });

        mapX.SetArray(mx);
        mapY.SetArray(my);

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
    // NUOVO: RADIAL WEIGHTED MODEL (R.W.M.) - ANALYTICAL NORMALIZATION
    // =========================================================

    public void ApplyRadialWeightedModel(Mat src, Mat dst, double backgroundValue = 0, int maxFilterRadius = 0)
    {
        bool isDouble = src.Depth() == MatType.CV_64F;
        MatType workType = isDouble ? MatType.CV_64F : MatType.CV_32F;
        
        using Mat srcWork = new Mat();
        src.ConvertTo(srcWork, workType);
        
        using Mat cleanSrc = srcWork.Clone();
        SanitizeInPlace(cleanSrc, isDouble);

        // 2. Auto-Background
        double bgToUse = backgroundValue;
        if (bgToUse <= 0) bgToUse = EstimateSkyBackground(cleanSrc, isDouble);

        // 3. Calcolo R.W.M. RAW
        using Mat rwmResult = new Mat(src.Size(), workType);
        if (isDouble) RunRWMCoreDouble(cleanSrc, rwmResult, bgToUse);
        else RunRWMCoreFloat(cleanSrc, rwmResult, bgToUse);

        // 4. Applicazione
        dst.Create(src.Size(), workType);
        
        int w = src.Cols; int h = src.Rows;
        int diag = (int)Math.Ceiling(Math.Sqrt(w * w + h * h) / 2.0);
        int maskRadius = (maxFilterRadius > 0 && maxFilterRadius < diag) ? maxFilterRadius : diag;

        if (maxFilterRadius > 0 && maxFilterRadius < diag)
        {
            // A. RINORMALIZZAZIONE ANALITICA
            // Vogliamo riportare l'RWM alla stessa scala dell'originale al raggio di taglio.
            // Formula: RWM_Norm = (RWM / Radius) + BG
            // Questo usa ConvertTo: alpha = 1.0/radius, beta = bgToUse.
            
            double scale = 1.0 / maskRadius;
            
            using Mat rwmNormalized = new Mat();
            // FIX CRASH: Usiamo ConvertTo invece di ScaleAdd
            // Dest = Src * alpha + beta
            rwmResult.ConvertTo(rwmNormalized, -1, scale, bgToUse);

            // B. Maschera Sfumata
            using Mat mask = new Mat(src.Size(), workType, Scalar.All(0));
            Cv2.Circle(mask, w/2, h/2, maskRadius, Scalar.All(1.0), -1, LineTypes.Link8);
            Cv2.GaussianBlur(mask, mask, new Size(25, 25), 0);

            // C. Blending
            // Output = rwmNormalized * Mask + Original * (1-Mask)
            using Mat termRwm = new Mat();
            Cv2.Multiply(rwmNormalized, mask, termRwm);

            using Mat maskInv = new Mat();
            Cv2.Subtract(new Scalar(1.0), mask, maskInv);
            
            using Mat termOrg = new Mat();
            Cv2.Multiply(srcWork, maskInv, termOrg);

            Cv2.Add(termRwm, termOrg, dst);
        }
        else
        {
            rwmResult.CopyTo(dst);
        }
    }

    // =========================================================
    // METODO M.C.M. (MEDIAN COMA MODEL) - FINAL
    // =========================================================

    public void ApplyMedianComaModel(Mat src, Mat dst, int maxFilterRadius = 0, int subsampling = 5)
    {
        int w = src.Cols; int h = src.Rows;
        Point2f center = new Point2f(w / 2.0f, h / 2.0f);
        int diag = (int)Math.Ceiling(Math.Sqrt(w * w + h * h) / 2.0);
        int maskRadius = (maxFilterRadius > 0 && maxFilterRadius < diag) ? maxFilterRadius : diag;

        bool isDouble = src.Depth() == MatType.CV_64F;
        MatType workType = isDouble ? MatType.CV_64F : MatType.CV_32F;

        using Mat srcWork = new Mat();
        src.ConvertTo(srcWork, workType);

        using Mat modelInput = srcWork.Clone();
        SanitizeInPlace(modelInput, isDouble); 

        int nTheta = 720 * Math.Max(1, subsampling / 2);
        using Mat polar = new Mat();
        Cv2.WarpPolar(modelInput, polar, new Size(diag, nTheta), center, diag, 
                      InterpolationFlags.Linear | InterpolationFlags.WarpFillOutliers, WarpPolarMode.Linear);

        using Mat polarTransposed = new Mat();
        Cv2.Transpose(polar, polarTransposed);
        
        if (isDouble) RunMedianModelConstructionDouble(polarTransposed);
        else RunMedianModelConstructionFloat(polarTransposed);

        using Mat polarModelInv = new Mat();
        Cv2.Transpose(polarTransposed, polarModelInv); 
        using Mat comaModelFull = new Mat();
        Cv2.WarpPolar(polarModelInv, comaModelFull, srcWork.Size(), center, diag, 
                      InterpolationFlags.Linear | InterpolationFlags.WarpInverseMap, WarpPolarMode.Linear);

        dst.Create(src.Size(), workType);

        if (maxFilterRadius > 0 && maxFilterRadius < diag)
        {
            double edgeValue = GetEdgeValueAtRadiusPolar(polarTransposed, maskRadius);

            using Mat comaModelShifted = new Mat();
            Cv2.Subtract(comaModelFull, new Scalar(edgeValue), comaModelShifted);

            using Mat mask = new Mat(src.Size(), workType, Scalar.All(0));
            Cv2.Circle(mask, (int)center.X, (int)center.Y, maskRadius, Scalar.All(1.0), -1, LineTypes.Link8);
            Cv2.GaussianBlur(mask, mask, new Size(25, 25), 0);

            using Mat subtractionTerm = new Mat();
            Cv2.Multiply(comaModelShifted, mask, subtractionTerm);
            Cv2.Subtract(srcWork, subtractionTerm, dst);
        }
        else
        {
            Cv2.Subtract(srcWork, comaModelFull, dst);
        }
    }

    // =========================================================
    // HELPER COMUNI
    // =========================================================

    private void SanitizeInPlace(Mat img, bool isDouble) {
        if (isDouble) {
            img.GetArray(out double[] arr);
            Parallel.For(0, arr.Length, i => { if (double.IsNaN(arr[i]) || double.IsInfinity(arr[i])) arr[i] = 0.0; });
            img.SetArray(arr);
        } else {
            img.GetArray(out float[] arr);
            Parallel.For(0, arr.Length, i => { if (float.IsNaN(arr[i]) || float.IsInfinity(arr[i])) arr[i] = 0.0f; });
            img.SetArray(arr);
        }
    }

    private double EstimateSkyBackground(Mat src, bool isDouble)
    {
        int w = src.Cols; int h = src.Rows;
        int boxW = Math.Max(5, w / 15); int boxH = Math.Max(5, h / 15);
        Rect[] corners = { new Rect(0,0,boxW,boxH), new Rect(w-boxW,0,boxW,boxH), new Rect(0,h-boxH,boxW,boxH), new Rect(w-boxW,h-boxH,boxW,boxH) };
        double minMedian = double.MaxValue;
        foreach(var r in corners) {
            using Mat roi = src.SubMat(r);
            double m = GetMedianOfMat(roi, isDouble);
            if (m < minMedian) minMedian = m;
        }
        return minMedian == double.MaxValue ? 0.0 : minMedian;
    }

    private double GetMedianOfMat(Mat m, bool isDouble) {
        if(isDouble) {
            m.GetArray(out double[] a); Array.Sort(a);
            return a.Length > 0 ? a[a.Length/2] : 0.0;
        } else {
            m.GetArray(out float[] a); Array.Sort(a);
            return a.Length > 0 ? a[a.Length/2] : 0.0;
        }
    }

    private double GetEdgeValueAtRadiusPolar(Mat polarTransposed, int radius) {
        int rIndex = Math.Min(radius, polarTransposed.Rows - 1); rIndex = Math.Max(0, rIndex);
        if (polarTransposed.Depth() == MatType.CV_64F) return polarTransposed.At<double>(rIndex, 0);
        else return polarTransposed.At<float>(rIndex, 0);
    }

    // =========================================================
    // CORE METHODS R.W.M.
    // =========================================================

    private void RunRWMCoreDouble(Mat src, Mat dst, double bg) {
        int rows = src.Rows; int cols = src.Cols; double cx = cols/2.0; double cy = rows/2.0;
        src.GetArray(out double[] sArr); double[] dArr = new double[sArr.Length];
        Parallel.For(0, rows, y => {
            int rowOffset = y * cols; double dy = y - cy; double dySq = dy * dy;
            for (int x = 0; x < cols; x++) {
                double dx = x - cx; double r = Math.Sqrt(dx * dx + dySq); if (r < 1.0) r = 1.0;
                dArr[rowOffset + x] = (sArr[rowOffset + x] - bg) * r;
            }
        }); dst.SetArray(dArr);
    }

    private void RunRWMCoreFloat(Mat src, Mat dst, double bg) {
        int rows = src.Rows; int cols = src.Cols; double cx = cols/2.0; double cy = rows/2.0;
        src.GetArray(out float[] sArr); float[] dArr = new float[sArr.Length]; float bgF = (float)bg;
        Parallel.For(0, rows, y => {
            int rowOffset = y * cols; double dy = y - cy; double dySq = dy * dy;
            for (int x = 0; x < cols; x++) {
                double dx = x - cx; double r = Math.Sqrt(dx * dx + dySq); if (r < 1.0) r = 1.0;
                dArr[rowOffset + x] = (float)((sArr[rowOffset + x] - bgF) * r);
            }
        }); dst.SetArray(dArr);
    }

    // =========================================================
    // CORE METHODS MCM
    // =========================================================

    private void RunMedianModelConstructionDouble(Mat polar) {
        int nRad = polar.Rows; int nTheta = polar.Cols; polar.GetArray(out double[] arr);
        Parallel.For(0, nRad, (int i) => {
            int rowOffset = i * nTheta; double[] buffer = ArrayPool<double>.Shared.Rent(nTheta); int vc = 0;
            try { for (int j = 0; j < nTheta; j++) { double v = arr[rowOffset + j]; if (Math.Abs(v) > 1e-9 && !double.IsNaN(v)) buffer[vc++] = v; }
                if (vc > 0) { Array.Sort(buffer, 0, vc); double median = buffer[vc / 2]; for (int j = 0; j < nTheta; j++) arr[rowOffset + j] = median; }
                else { for (int j = 0; j < nTheta; j++) arr[rowOffset + j] = 0; }
            } finally { ArrayPool<double>.Shared.Return(buffer); }
        }); polar.SetArray(arr);
    }

    private void RunMedianModelConstructionFloat(Mat polar) {
        int nRad = polar.Rows; int nTheta = polar.Cols; polar.GetArray(out float[] arr);
        Parallel.For(0, nRad, (int i) => {
            int rowOffset = i * nTheta; double[] buffer = ArrayPool<double>.Shared.Rent(nTheta); int vc = 0;
            try { for (int j = 0; j < nTheta; j++) { double v = arr[rowOffset + j]; if (Math.Abs(v) > 1e-9 && !double.IsNaN(v)) buffer[vc++] = v; }
                if (vc > 0) { Array.Sort(buffer, 0, vc); float median = (float)buffer[vc / 2]; for (int j = 0; j < nTheta; j++) arr[rowOffset + j] = median; }
                else { for (int j = 0; j < nTheta; j++) arr[rowOffset + j] = 0; }
            } finally { ArrayPool<double>.Shared.Return(buffer); }
        }); polar.SetArray(arr);
    }

    // =========================================================
    // VECCHI CORE METHODS
    // =========================================================

    private void RunRVSFCoreDouble(Mat src, Mat dst, double A, double B, double N, IProgress<double> progress) {
        int rows = src.Rows; int cols = src.Cols; double xNuc = cols / 2.0; double yNuc = rows / 2.0;
        src.GetArray(out double[] sArr); double[] dArr = new double[sArr.Length]; int completed = 0;
        Parallel.For(0, rows, (int y) => {
            int rowOffset = y * cols; double dy = y - yNuc; double dySq = dy * dy;
            for (int x = 0; x < cols; x++) {
                double dx = x - xNuc; double rho = Math.Sqrt(dx * dx + dySq);
                int r = (int)Math.Round(A + (B * Math.Pow(rho, N))); if (r < 1) r = 1;
                int yMin = (y - r < 0) ? 0 : (y - r >= rows ? rows - 1 : y - r); int yMax = (y + r < 0) ? 0 : (y + r >= rows ? rows - 1 : y + r);
                int xMin = (x - r < 0) ? 0 : (x - r >= cols ? cols - 1 : x - r); int xMax = (x + r < 0) ? 0 : (x + r >= cols ? cols - 1 : x + r);
                double valC = sArr[rowOffset + x];
                double sE = sArr[yMin * cols + x] + sArr[yMax * cols + x] + sArr[y * cols + xMin] + sArr[y * cols + xMax];
                double sC = sArr[yMin * cols + xMin] + sArr[yMin * cols + xMax] + sArr[yMax * cols + xMin] + sArr[yMax * cols + xMax];
                dArr[rowOffset + x] = (1024.0 * valC) - (192.0 * sE) - (64.0 * sC);
            }
            if (progress != null) { var current = Interlocked.Increment(ref completed); if (current % 100 == 0) progress.Report((double)current / rows * 100); }
        }); dst.SetArray(dArr);
    }

    private void RunRVSFCoreFloat(Mat src, Mat dst, double A, double B, double N, IProgress<double> progress) {
        int rows = src.Rows; int cols = src.Cols; double xNuc = cols / 2.0; double yNuc = rows / 2.0;
        src.GetArray(out float[] sArr); float[] dArr = new float[sArr.Length]; int completed = 0;
        Parallel.For(0, rows, (int y) => {
            int rowOffset = y * cols; double dy = y - yNuc; double dySq = dy * dy;
            for (int x = 0; x < cols; x++) {
                double dx = x - xNuc; double rho = Math.Sqrt(dx * dx + dySq);
                int r = (int)Math.Round(A + (B * Math.Pow(rho, N))); if (r < 1) r = 1;
                int yMin = (y - r < 0) ? 0 : (y - r >= rows ? rows - 1 : y - r); int yMax = (y + r < 0) ? 0 : (y + r >= rows ? rows - 1 : y + r);
                int xMin = (x - r < 0) ? 0 : (x - r >= cols ? cols - 1 : x - r); int xMax = (x + r < 0) ? 0 : (x + r >= cols ? cols - 1 : x + r);
                float valC = sArr[rowOffset + x];
                float sE = sArr[yMin * cols + x] + sArr[yMax * cols + x] + sArr[y * cols + xMin] + sArr[y * cols + xMax];
                float sC = sArr[yMin * cols + xMin] + sArr[yMin * cols + xMax] + sArr[yMax * cols + xMin] + sArr[yMax * cols + xMax];
                dArr[rowOffset + x] = (float)((1024.0 * valC) - (192.0 * sE) - (64.0 * sC));
            }
            if (progress != null) { var current = Interlocked.Increment(ref completed); if (current % 100 == 0) progress.Report((double)current / rows * 100); }
        }); dst.SetArray(dArr);
    }
    
    private void InverseRhoCoreDouble(Mat src, Mat dst, int subsampling) {
        int rows = src.Rows; int cols = src.Cols; double xNuc = cols / 2.0; double yNuc = rows / 2.0;
        src.GetArray(out double[] sArr); double[] dArr = new double[sArr.Length];
        Scalar mean = Cv2.Mean(src); double renorm = 100.0 / (Math.Abs(mean.Val0) > 1e-9 ? mean.Val0 : 1.0);
        Parallel.For(0, rows, (int y) => {
            int rowOffset = y * cols;
            for (int x = 0; x < cols; x++) {
                double rhoSum = 0; for (int m = 0; m < 10; m++) { double dy = (y + 0.5 - yNuc) - 0.45 + (m * 0.1); for (int n = 0; n < 10; n++) { double dx = (x + 0.5 - xNuc) - 0.45 + (n * 0.1); rhoSum += Math.Sqrt(dx * dx + dy * dy); } }
                dArr[rowOffset + x] = sArr[rowOffset + x] * renorm * (rhoSum * 0.01);
            }
        }); dst.SetArray(dArr);
    }

    private void InverseRhoCoreFloat(Mat src, Mat dst, int subsampling) {
        int rows = src.Rows; int cols = src.Cols; double xNuc = cols / 2.0; double yNuc = rows / 2.0;
        src.GetArray(out float[] sArr); float[] dArr = new float[sArr.Length];
        Scalar mean = Cv2.Mean(src); double renorm = 100.0 / (Math.Abs(mean.Val0) > 1e-9 ? mean.Val0 : 1.0);
        Parallel.For(0, rows, (int y) => {
            int rowOffset = y * cols;
            for (int x = 0; x < cols; x++) {
                double rhoSum = 0; for (int m = 0; m < 10; m++) { double dy = (y + 0.5 - yNuc) - 0.45 + (m * 0.1); for (int n = 0; n < 10; n++) { double dx = (x + 0.5 - xNuc) - 0.45 + (n * 0.1); rhoSum += Math.Sqrt(dx * dx + dy * dy); } }
                dArr[rowOffset + x] = (float)(sArr[rowOffset + x] * renorm * (rhoSum * 0.01));
            }
        }); dst.SetArray(dArr);
    }

    private void AzimuthalAverageCoreDouble(Mat polar, double rejSig) {
        int nRad = polar.Rows; int nTheta = polar.Cols; polar.GetArray(out double[] arr);
        Parallel.For(0, nRad, (int i) => {
            int rowOffset = i * nTheta; double sum = 0, sumSq = 0; int count = 0;
            for (int j = 0; j < nTheta; j++) { double v = arr[rowOffset + j]; if (!double.IsNaN(v) && v >= 0) { sum += v; sumSq += v * v; count++; } }
            if (count < 2) return; double m1 = sum / count; double s1 = Math.Sqrt(Math.Max(0, (sumSq / count) - (m1 * m1))); double rMin = Math.Max(0, m1 - rejSig * s1), rMax = m1 + rejSig * s1;
            double cSum = 0; int cCount = 0; for (int j = 0; j < nTheta; j++) { double v = arr[rowOffset + j]; if (v >= rMin && v <= rMax) { cSum += v; cCount++; } }
            double finalMean = cCount > 0 ? cSum / cCount : m1; double div = finalMean + 1e-9; for (int j = 0; j < nTheta; j++) arr[rowOffset + j] /= div;
        }); polar.SetArray(arr);
    }

    private void AzimuthalMedianCoreDouble(Mat polar) {
        int nRad = polar.Rows; int nTheta = polar.Cols; polar.GetArray(out double[] arr);
        Parallel.For(0, nRad, (int i) => {
            int rowOffset = i * nTheta; double[] buffer = ArrayPool<double>.Shared.Rent(nTheta); int vc = 0;
            try { for (int j = 0; j < nTheta; j++) { double v = arr[rowOffset + j]; if (!double.IsNaN(v)) buffer[vc++] = v; }
                if (vc > 0) { Array.Sort(buffer, 0, vc); double median = buffer[vc / 2]; double div = median + 1e-9; for (int j = 0; j < nTheta; j++) arr[rowOffset + j] /= div; }
            } finally { ArrayPool<double>.Shared.Return(buffer); }
        }); polar.SetArray(arr);
    }

    private void AzimuthalMedianCoreFloat(Mat polar) {
        int nRad = polar.Rows; int nTheta = polar.Cols; polar.GetArray(out float[] arr);
        Parallel.For(0, nRad, (int i) => {
            int rowOffset = i * nTheta; double[] buffer = ArrayPool<double>.Shared.Rent(nTheta); int vc = 0;
            try { for (int j = 0; j < nTheta; j++) { double v = arr[rowOffset + j]; if (!double.IsNaN(v)) buffer[vc++] = v; }
                if (vc > 0) { Array.Sort(buffer, 0, vc); double median = buffer[vc / 2]; double div = median + 1e-9; for (int j = 0; j < nTheta; j++) arr[rowOffset + j] = (float)(arr[rowOffset + j] / div); }
            } finally { ArrayPool<double>.Shared.Return(buffer); }
        }); polar.SetArray(arr);
    }

    private void AzimuthalRenormCoreDouble(Mat polar, double rejSig, double nSig) {
        int nRad = polar.Rows; int nTheta = polar.Cols; polar.GetArray(out double[] arr);
        Parallel.For(0, nRad, (int i) => {
            int rowOffset = i * nTheta; double sum = 0, sumSq = 0; int count = 0;
            for (int j = 0; j < nTheta; j++) { double v = arr[rowOffset + j]; if (v >= 0) { sum += v; sumSq += v * v; count++; } }
            if (count < 3) return; double m1 = sum / count; double s1 = Math.Sqrt(Math.Max(0, (sumSq / count) - (m1 * m1))); double rMin = m1 - rejSig * s1, rMax = m1 + rejSig * s1;
            double cSum = 0, cSumSq = 0; int cc = 0; for (int j = 0; j < nTheta; j++) { double v = arr[rowOffset + j]; if (v >= rMin && v <= rMax) { cSum += v; cSumSq += v * v; cc++; } }
            if (cc < 2) return; double fm = cSum / cc; double fs = Math.Sqrt(Math.Max(0, (cSumSq / cc) - (fm * fm))); double rowMin = fm - nSig * fs, rowMax = fm + nSig * fs; double diff = Math.Max(1e-9, rowMax - rowMin);
            for (int j = 0; j < nTheta; j++) arr[rowOffset + j] = (arr[rowOffset + j] - rowMin) / diff;
        }); polar.SetArray(arr);
    }

    private void AzimuthalAverageCoreFloat(Mat polar, double rejSig) { AzimuthalAverageCoreDouble(polar, rejSig); }
    private void AzimuthalRenormCoreFloat(Mat polar, double rs, double ns) { AzimuthalRenormCoreDouble(polar, rs, ns); }

    private Mat PrepareImageForRVSF(Mat src, bool useLog) {
        Mat w = new Mat(); if(!useLog) src.ConvertTo(w, MatType.CV_64F);
        else { src.ConvertTo(w, MatType.CV_64F); Cv2.Add(w, Scalar.All(1.0), w); Cv2.Log(w, w); w.ConvertTo(w, -1, 1.0/Math.Log(10)); }
        return w;
    }
}