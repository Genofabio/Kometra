using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using MathNet.Numerics;
using MathNet.Numerics.LinearAlgebra;
using OpenCvSharp;
using Point = Avalonia.Point;
using Rect = Avalonia.Rect;

namespace KomaLab.Services.Imaging;

public class ImageAnalysisService : IImageAnalysisService
{
    // =======================================================================
    // 1. STATISTICHE & UTILS
    // =======================================================================

    public (double Mean, double StdDev) ComputeStatistics(Mat image)
    {
        if (image.Empty()) return (0, 1);
        using Mat meanMat = new Mat();
        using Mat stdDevMat = new Mat();
        using Mat mask = new Mat();
        Cv2.Compare(image, image, mask, CmpType.EQ);
        Cv2.MeanStdDev(image, meanMat, stdDevMat, mask);
        double mean = meanMat.Get<double>(0, 0);
        double std = stdDevMat.Get<double>(0, 0);
        if (double.IsNaN(mean)) mean = 0;
        if (double.IsNaN(std) || std < 1e-9) std = 1.0;
        return (mean, std);
    }
    
    public Rect FindValidDataBox(Mat image)
    {
        using Mat mask = new Mat();
        Cv2.Compare(image, image, mask, CmpType.EQ);
        var rect = Cv2.BoundingRect(mask);
        return new Rect(rect.X, rect.Y, rect.Width, rect.Height);
    }

    // =======================================================================
    // 2. CENTRAMENTO LOCALE (Manuale / Cometa)
    // =======================================================================

    public Point FindCenterOfLocalRegion(Mat regionMat)
    {
        if (regionMat.Empty()) return new Point(0, 0);
        using Mat workingMat = new Mat();
        if (regionMat.Type() != MatType.CV_64FC1) regionMat.ConvertTo(workingMat, MatType.CV_64FC1);
        else regionMat.CopyTo(workingMat);

        using Mat statsMat = new Mat();
        Cv2.GaussianBlur(workingMat, statsMat, new Size(3, 3), 0);
        Cv2.MinMaxLoc(statsMat, out double minVal, out double maxVal);
        
        if ((maxVal - minVal) <= 1e-6) return new Point(workingMat.Width / 2.0, workingMat.Height / 2.0);

        var (mean, sigma) = ComputeStatistics(workingMat);
        double threshold = mean + (sigma * 3.0);
        int minArea = 8;

        using Mat maskF = new Mat();
        Cv2.Threshold(workingMat, maskF, threshold, 1.0, ThresholdTypes.Binary);
        using Mat mask8U = new Mat();
        maskF.ConvertTo(mask8U, MatType.CV_8UC1, 255.0);

        using Mat labels = new Mat();
        using Mat stats = new Mat();
        using Mat centroids = new Mat();
        int numFeatures = Cv2.ConnectedComponentsWithStats(mask8U, labels, stats, centroids);

        if (numFeatures <= 1) return new Point(workingMat.Width / 2.0, workingMat.Height / 2.0);

        int bestLabel = -1, maxArea = -1;
        for (int i = 1; i < numFeatures; i++)
        {
            int area = stats.At<int>(i, (int)ConnectedComponentsTypes.Area);
            if (area >= minArea && area > maxArea) { maxArea = area; bestLabel = i; }
        }

        if (bestLabel == -1) return new Point(workingMat.Width / 2.0, workingMat.Height / 2.0);

        int bX = stats.At<int>(bestLabel, (int)ConnectedComponentsTypes.Left);
        int bY = stats.At<int>(bestLabel, (int)ConnectedComponentsTypes.Top);
        int bW = stats.At<int>(bestLabel, (int)ConnectedComponentsTypes.Width);
        int bH = stats.At<int>(bestLabel, (int)ConnectedComponentsTypes.Height);
        
        int pX = Math.Max(0, bX - 2);
        int pY = Math.Max(0, bY - 2);
        int pW = Math.Min(workingMat.Cols - pX, bW + 4);
        int pH = Math.Min(workingMat.Rows - pY, bH + 4);

        Rect starRect = new Rect(pX, pY, pW, pH);
        using Mat starCrop = new Mat(workingMat, new OpenCvSharp.Rect(pX, pY, pW, pH));
        Point centerInStarCrop;
        try { centerInStarCrop = FindGaussianCenter(starCrop); }
        catch { centerInStarCrop = new Point(starRect.Width / 2.0, starRect.Height / 2.0); }

        return new Point(centerInStarCrop.X + starRect.X, centerInStarCrop.Y + starRect.Y);
    }

    // =======================================================================
    // 3. ALGORITMI CORE
    // =======================================================================

    public Point FindGaussianCenter(Mat rawMat, double sigma = 3.0)
    {
        if (rawMat.Empty()) return new Point(-1, -1);
        using Mat workingMat = new Mat();
        if (rawMat.Type() != MatType.CV_64FC1) rawMat.ConvertTo(workingMat, MatType.CV_64FC1);
        else rawMat.CopyTo(workingMat);

        using Mat smoothedMat = new Mat();
        if (sigma > 0) Cv2.GaussianBlur(workingMat, smoothedMat, new Size(0, 0), sigma, sigma);
        else workingMat.CopyTo(smoothedMat);
        
        Cv2.MinMaxLoc(smoothedMat, out double minVal, out double maxVal);
        if ((maxVal - minVal) <= 1e-6) return FindPeak(smoothedMat, sigma: 0);

        double scaleFactor = 1.0 / (maxVal - minVal);
        double threshold = minVal + ((maxVal - minVal) * 0.2);

        using Mat maskF = new Mat();
        Cv2.Threshold(smoothedMat, maskF, threshold, 1.0, ThresholdTypes.Binary);
        using Mat mask8U = new Mat();
        maskF.ConvertTo(mask8U, MatType.CV_8UC1);
        using Mat locationsMat = new Mat();
        Cv2.FindNonZero(mask8U, locationsMat);
        
        if (locationsMat.Total() < 6) return FindPeak(smoothedMat, sigma: 0);
        locationsMat.GetArray(out OpenCvSharp.Point[] locations);

        List<double[]> xDataList = new List<double[]>();
        List<double> yDataList = new List<double>();
        var indexer = smoothedMat.GetGenericIndexer<double>();
        double xoInitNum = 0, yoInitNum = 0, weightSum = 0;

        foreach (var p in locations)
        {
            double normVal = (indexer[p.Y, p.X] - minVal) * scaleFactor;
            xDataList.Add(new double[] { p.X, p.Y });
            yDataList.Add(normVal);
            double w = Math.Max(0, normVal);
            xoInitNum += p.X * w;
            yoInitNum += p.Y * w;
            weightSum += w;
        }

        if (weightSum <= 1e-9) return FindPeak(smoothedMat, sigma: 0);

        double[][] xData = xDataList.ToArray();
        double[] yData = yDataList.ToArray();
        
        var initialGuess = Vector<double>.Build.Dense([1.0, xoInitNum / weightSum, yoInitNum / weightSum, 3.0, 3.0, 0.0]);
        var lowerBounds = Vector<double>.Build.Dense([0.0, 0, 0, 0.1, 0.1, -0.2]);
        var upperBounds = Vector<double>.Build.Dense([1.5, smoothedMat.Width, smoothedMat.Height, smoothedMat.Width, smoothedMat.Height, 0.5]);

        try
        {
            var yDataVector = Vector<double>.Build.Dense(yData);
            Func<Vector<double>, double> objectiveFunc = (p) =>
            {
                double amp = p[0], xo = p[1], yo = p[2], sX = Math.Max(Math.Abs(p[3]), 1e-6), sY = Math.Max(Math.Abs(p[4]), 1e-6), off = p[5];
                var model = Vector<double>.Build.Dense(xData.Length, i =>
                {
                    double dx = (xData[i][0] - xo);
                    double dy = (xData[i][1] - yo);
                    return off + amp * Math.Exp(-0.5 * ((dx * dx) / (sX * sX) + (dy * dy) / (sY * sY)));
                });
                return (yDataVector - model).L2Norm();
            };
            
            var result = FindMinimum.OfFunctionConstrained(objectiveFunc, lowerBounds, upperBounds, initialGuess, maxIterations: 500);
            return new Point(result[1], result[2]);
        }
        catch { return FindPeak(smoothedMat, sigma: 0); }
    }

    public Point FindPeak(Mat rawMat, double sigma = 1.0)
    {
        if (rawMat.Empty()) return new Point(-1, -1);
        using Mat workingMat = new Mat();
        if (rawMat.Type() != MatType.CV_64FC1) rawMat.ConvertTo(workingMat, MatType.CV_64FC1);
        else rawMat.CopyTo(workingMat);
        using Mat blurredMat = new Mat();
        if (sigma > 0) Cv2.GaussianBlur(workingMat, blurredMat, new Size(0, 0), sigma, sigma);
        else workingMat.CopyTo(blurredMat);
        
        Cv2.MinMaxLoc(blurredMat, out _, out _, out _, out OpenCvSharp.Point maxLoc);
        int x0 = maxLoc.X, y0 = maxLoc.Y;
        
        if (y0 > 0 && x0 > 0 && y0 < blurredMat.Rows - 1 && x0 < blurredMat.Cols - 1)
        {
            try
            {
                var idx = blurredMat.GetGenericIndexer<double>();
                double c = idx[y0, x0], r = idx[y0, x0 + 1], l = idx[y0, x0 - 1], u = idx[y0 - 1, x0], d = idx[y0 + 1, x0];
                double dxx = r + l - 2 * c;
                double dyy = d + u - 2 * c;
                double subX = x0, subY = y0;
                if (Math.Abs(dxx) >= 1e-9) subX = x0 - ((r - l) / (2.0 * dxx));
                if (Math.Abs(dyy) >= 1e-9) subY = y0 - ((d - u) / (2.0 * dyy));
                return new Point(subX, subY);
            }
            catch { }
        }
        return new Point(x0, y0);
    }

    public Point FindCentroid(Mat rawMat, double sigma = 5.0)
    {
        if (rawMat.Empty()) return new Point(-1, -1);
        using Mat workingMat = new Mat();
        if (rawMat.Type() != MatType.CV_64FC1) rawMat.ConvertTo(workingMat, MatType.CV_64FC1);
        else rawMat.CopyTo(workingMat);
        using Mat blurredMat = new Mat();
        if (sigma > 0) { int k = (int)(sigma * 3) | 1; Cv2.GaussianBlur(workingMat, blurredMat, new Size(k, k), sigma); }
        else workingMat.CopyTo(blurredMat);
        Cv2.Normalize(blurredMat, blurredMat, 0, 1, NormTypes.MinMax);
        Moments moments = Cv2.Moments(blurredMat, binaryImage: false);
        if (moments.M00 != 0) return new Point(moments.M10 / moments.M00, moments.M01 / moments.M00);
        return new Point(blurredMat.Width / 2.0, blurredMat.Height / 2.0);
    }

    // =======================================================================
    // 4. ANALISI SPOSTAMENTO (ORB A GRIGLIA + RANSAC + BACKGROUND CUT)
    // =======================================================================

    public Point ComputeStarFieldShift(Mat reference, Mat target)
    {
        if (reference.Empty() || target.Empty()) return new Point(0, 0);

        // 1. Pre-Processing Intelligente
        using var ref8 = Ensure8Bit(reference);
        using var tgt8 = Ensure8Bit(target);
        
        if (ref8.Empty() || tgt8.Empty()) return new Point(0, 0);

        // 2. Feature Detection a GRIGLIA
        var kpsRef = DetectFeaturesInGrid(ref8, 2, 2);
        var kpsTgt = DetectFeaturesInGrid(tgt8, 2, 2);

        if (kpsRef.Length < 10 || kpsTgt.Length < 10) return new Point(0, 0);

        // 3. Estrazione Descrittori
        using var orb = ORB.Create();
        using var descRef = new Mat();
        using var descTgt = new Mat();
        
        orb.Compute(ref8, ref kpsRef, descRef);
        orb.Compute(tgt8, ref kpsTgt, descTgt);

        if (descRef.Empty() || descTgt.Empty()) return new Point(0, 0);

        // 4. Matching KNN + Ratio Test
        using var matcher = new BFMatcher(NormTypes.Hamming, crossCheck: false);
        DMatch[][] matches = matcher.KnnMatch(descRef, descTgt, k: 2);

        List<Point2f> ptsRef = new List<Point2f>();
        List<Point2f> ptsTgt = new List<Point2f>();
        float ratioThresh = 0.75f;

        foreach (var matchGroup in matches)
        {
            if (matchGroup.Length >= 2)
            {
                if (matchGroup[0].Distance < ratioThresh * matchGroup[1].Distance)
                {
                    ptsRef.Add(kpsRef[matchGroup[0].QueryIdx].Pt);
                    ptsTgt.Add(kpsTgt[matchGroup[0].TrainIdx].Pt);
                }
            }
        }

        if (ptsRef.Count < 5) return new Point(0, 0);

        // 5. RANSAC
        using var inliers = new Mat();
        Mat? transform = Cv2.EstimateAffinePartial2D(
            InputArray.Create(ptsTgt), 
            InputArray.Create(ptsRef), 
            inliers, 
            RobustEstimationAlgorithms.RANSAC, 
            ransacReprojThreshold: 5.0
        );

        if (transform.Empty()) return new Point(0, 0);

        double tx = transform.At<double>(0, 2);
        double ty = transform.At<double>(1, 2);

        // Sanity Check
        if (Math.Abs(tx) > reference.Width / 3.0 || Math.Abs(ty) > reference.Height / 3.0)
            return new Point(0, 0);

        return new Point(tx, ty);
    }

    // --- HELPER: RILEVAMENTO A GRIGLIA CORRETTO ---
    private KeyPoint[] DetectFeaturesInGrid(Mat img, int rows, int cols)
    {
        using var detector = ORB.Create(nFeatures: 500); 
        var allKeypoints = new List<KeyPoint>();

        int cellW = img.Width / cols;
        int cellH = img.Height / rows;

        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                // Calcolo coordinate base
                int x = c * cellW;
                int y = r * cellH;
                
                // Calcolo larghezza/altezza gestendo i resti (ultimo blocco prende tutto)
                int w = (c == cols - 1) ? img.Width - x : cellW;
                int h = (r == rows - 1) ? img.Height - y : cellH;

                var cvRect = new OpenCvSharp.Rect(x, y, w, h);
                using Mat cellMat = new Mat(img, cvRect);

                // FIX: Detect restituisce l'array, niente 'out'
                KeyPoint[] cellKps = detector.Detect(cellMat);

                // Trasla coordinate da Locali a Globali
                for (int k = 0; k < cellKps.Length; k++)
                {
                    cellKps[k].Pt.X += x;
                    cellKps[k].Pt.Y += y;
                    allKeypoints.Add(cellKps[k]);
                }
            }
        }
        return allKeypoints.ToArray();
    }

    // --- HELPER: CONVERSIONE CON PULIZIA FONDO CIELO ---
    private Mat Ensure8Bit(Mat input)
    {
        if (input.Type() == MatType.CV_8UC1) return input.Clone();

        using var floatMat = new Mat();
        input.ConvertTo(floatMat, MatType.CV_32FC1);
        Cv2.PatchNaNs(floatMat, 0);

        // 1. Calcolo Statistiche 
        using var meanMat = new Mat();
        using var stdDevMat = new Mat();
        Cv2.MeanStdDev(floatMat, meanMat, stdDevMat);
        double mean = meanMat.Get<double>(0, 0);
        double std = stdDevMat.Get<double>(0, 0);

        // 2. Sottrazione del Fondo (Media + 1 Sigma)
        double blackPoint = mean + std; 
        using var subMat = new Mat();
        Cv2.Subtract(floatMat, new Scalar(blackPoint), subMat);
        
        // 3. Logaritmo
        using var tempLog = new Mat();
        Cv2.Add(subMat, new Scalar(1.0), tempLog); 
        Cv2.Log(tempLog, tempLog);

        // 4. Normalizzazione
        Mat result8 = new Mat();
        Cv2.Normalize(tempLog, result8, 0, 255, NormTypes.MinMax, (int)MatType.CV_8UC1);

        return result8;
    }
}