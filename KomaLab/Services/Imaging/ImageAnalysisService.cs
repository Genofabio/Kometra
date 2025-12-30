using System;
using System.Collections.Generic;
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
        
        // Compare crea una maschera per escludere eventuali valori non validi se necessario
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
    // 3. ALGORITMI CORE (GAUSSIAN FITTING)
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
    // 4. ANALISI SPOSTAMENTO CAMPO STELLARE (PIPELINE OTTIMIZZATA)
    // =======================================================================
    public Point ComputeStarFieldShift(Mat reference, Mat target)
    {
        if (reference.Empty() || target.Empty()) return new Point(0, 0);

        // 1. Pre-Processing: TOPHAT + SOBEL
        using var refEdge = ApplySobelFilter(reference);
        using var tgtEdge = ApplySobelFilter(target);

        int rows = 3;
        int cols = 3;
        
        List<Point2d> shifts = new List<Point2d>(rows * cols); // Capacity pre-allocata

        int cellW = refEdge.Width / cols;
        int cellH = refEdge.Height / rows;

        // --- RISORSE RIUTILIZZABILI (Allocazione fuori dal loop per performance) ---
        // Essendo variabili locali dentro il metodo, non serve IDisposable sulla classe.
        using var hanningWin = new Mat();
        Cv2.CreateHanningWindow(hanningWin, new Size(cellW, cellH), MatType.CV_32FC1);

        using var meanMat = new Mat();
        using var stdDevMat = new Mat();
        
        using var cellRef32 = new Mat();
        using var cellTgt32 = new Mat();

        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                int x = c * cellW;
                int y = r * cellH;

                // Safety Check
                if (x + cellW > refEdge.Width || y + cellH > refEdge.Height) continue;

                var rect = new OpenCvSharp.Rect(x, y, cellW, cellH);
                
                // Estrazione Cella (Lightweight header)
                using var cellRef = new Mat(refEdge, rect);
                using var cellTgt = new Mat(tgtEdge, rect);
                
                // Conversione sicura nei buffer pre-allocati
                if (cellRef.Type() != MatType.CV_32FC1) cellRef.ConvertTo(cellRef32, MatType.CV_32FC1);
                else cellRef.CopyTo(cellRef32);

                if (cellTgt.Type() != MatType.CV_32FC1) cellTgt.ConvertTo(cellTgt32, MatType.CV_32FC1);
                else cellTgt.CopyTo(cellTgt32);

                // ============================================================
                // OTTIMIZZAZIONE 2: SMART GRID / VALIDAZIONE CONTENUTO
                // ============================================================
                
                // Statistiche (Riutilizzando i Mat allocati fuori)
                Cv2.MeanStdDev(cellRef32, meanMat, stdDevMat);
                
                double cellMean = meanMat.Get<double>(0, 0);
                double cellStd = stdDevMat.Get<double>(0, 0);

                // MinMaxLoc veloce
                Cv2.MinMaxLoc(cellRef32, out _, out double maxVal);

                // LOGICA DI SCARTO:
                // Se il picco max non supera significativamente il fondo, è rumore.
                double noiseThreshold = cellMean + (4.0 * cellStd);

                if (maxVal < noiseThreshold)
                {
                    continue; // Skip cella
                }
                // ============================================================

                double response;
                Point2d shift = Cv2.PhaseCorrelate(cellRef32, cellTgt32, hanningWin, out response);
                
                // Filtro qualità response
                if (response > 0.05) 
                {
                    if (Math.Abs(shift.X) < cellW / 2.0 && Math.Abs(shift.Y) < cellH / 2.0)
                    {
                        shifts.Add(shift);
                    }
                }
            }
        }

        if (shifts.Count == 0) return new Point(0, 0);

        // 3. AGGREGAZIONE: Consensus Clustering
        Point2d finalShift = ComputeConsensusShift(shifts);

        return new Point(finalShift.X, finalShift.Y); 
    }

    // --- HELPER 1: FILTRO MIGLIORATO (TOPHAT + SOBEL) ---
    private Mat ApplySobelFilter(Mat input)
    {
        // 1. Normalizzazione & Conversione (FITS friendly)
        using var grayFloat = new Mat();
        
        if (input.Channels() > 1) 
        {
            using var tempGray = new Mat();
            Cv2.CvtColor(input, tempGray, ColorConversionCodes.BGR2GRAY);
            tempGray.ConvertTo(grayFloat, MatType.CV_32FC1);
        }
        else 
        {
            input.ConvertTo(grayFloat, MatType.CV_32FC1);
        }

        // Normalizza tra 0 e 1 (fondamentale per MorphEx e Threshold)
        Cv2.Normalize(grayFloat, grayFloat, 0, 1, NormTypes.MinMax);

        // 2. Morphological Top-Hat 
        // Creiamo il kernel localmente.
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(15, 15));
        using var topHat = new Mat();
        Cv2.MorphologyEx(grayFloat, topHat, MorphTypes.TopHat, kernel);

        // 3. Gaussian Blur (Riduzione rumore)
        using var blurred = new Mat();
        Cv2.GaussianBlur(topHat, blurred, new Size(0, 0), 1.5);

        // 4. Sobel 
        using var gradX = new Mat();
        using var gradY = new Mat();
        
        Cv2.Sobel(blurred, gradX, MatType.CV_32FC1, 1, 0, ksize: 3);
        Cv2.Sobel(blurred, gradY, MatType.CV_32FC1, 0, 1, ksize: 3);

        var magnitude = new Mat();
        Cv2.Magnitude(gradX, gradY, magnitude);

        // 5. Pulizia Finale (Thresholding Adattivo)
        using var meanMat = new Mat();
        using var stdDevMat = new Mat();
        Cv2.MeanStdDev(magnitude, meanMat, stdDevMat);
        
        double mean = meanMat.Get<double>(0, 0);
        double std = stdDevMat.Get<double>(0, 0);
        
        Cv2.Threshold(magnitude, magnitude, mean + (2.0 * std), 0, ThresholdTypes.Tozero);

        return magnitude;
    }

    // --- HELPER 2: CONSENSUS CLUSTERING ---
    private Point2d ComputeConsensusShift(List<Point2d> points)
    {
        if (points.Count == 0) return new Point2d(0, 0);
        if (points.Count == 1) return points[0];

        List<Point2d> bestCluster = new List<Point2d>();
        double tolerance = 2.0; // Pixel di tolleranza

        foreach (var p1 in points)
        {
            var currentCluster = new List<Point2d>();
            
            foreach (var p2 in points)
            {
                double dist = Math.Sqrt(Math.Pow(p1.X - p2.X, 2) + Math.Pow(p1.Y - p2.Y, 2));
                if (dist <= tolerance)
                {
                    currentCluster.Add(p2);
                }
            }

            if (currentCluster.Count > bestCluster.Count)
            {
                bestCluster = currentCluster;
            }
        }

        if (bestCluster.Count == 0) return points[0];

        double sumX = 0, sumY = 0;
        foreach (var p in bestCluster)
        {
            sumX += p.X;
            sumY += p.Y;
        }

        return new Point2d(sumX / bestCluster.Count, sumY / bestCluster.Count);
    }
}