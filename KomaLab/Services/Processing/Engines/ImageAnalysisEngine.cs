using System;
using System.Collections.Generic;
using System.Linq;
using KomaLab.Models.Primitives;
using MathNet.Numerics;
using MathNet.Numerics.LinearAlgebra;
using OpenCvSharp;

namespace KomaLab.Services.Processing.Engines;

/// <summary>
/// Motore di analisi geometrica.
/// Implementazione: "New Architecture" (Mat) con Algoritmi "Old Version" (Funzionanti).
/// </summary>
public class ImageAnalysisEngine : IImageAnalysisEngine
{
    public ImageAnalysisEngine() { }

    // =======================================================================
    // 1. DISCOVERY & BOUNDING
    // =======================================================================

    public Rect2D FindValidDataBox(Mat image)
    {
        if (image == null || image.Empty()) return new Rect2D(0, 0, 0, 0);

        using Mat mask = new Mat();
        // Vecchia logica: Compare EQ per trovare i non-NaN
        Cv2.Compare(image, image, mask, CmpType.EQ); 
        var rect = Cv2.BoundingRect(mask);
        
        return new Rect2D(rect.X, rect.Y, rect.Width, rect.Height);
    }

    // =======================================================================
    // 2. CENTROIDING (LOGICA "OLD" RIPRISTINATA)
    // =======================================================================

    public Point2D FindCenterOfLocalRegion(Mat region)
    {
        if (region == null || region.Empty()) return new Point2D(0, 0);

        // 1. Conversione sicura
        using Mat workingMat = new Mat();
        if (region.Type() != MatType.CV_64FC1) region.ConvertTo(workingMat, MatType.CV_64FC1);
        else region.CopyTo(workingMat);

        // 2. Pre-processing per statistiche
        using Mat statsMat = new Mat();
        Cv2.GaussianBlur(workingMat, statsMat, new Size(3, 3), 0);
        Cv2.MinMaxLoc(statsMat, out double minVal, out double maxVal);

        // Se l'immagine è piatta, ritorna il centro geometrico
        if ((maxVal - minVal) <= 1e-6) 
            return new Point2D(workingMat.Width / 2.0, workingMat.Height / 2.0);

        // 3. Calcolo Soglia (Mean + 3*Sigma)
        using Mat tempMask = new Mat();
        Cv2.Compare(workingMat, workingMat, tempMask, CmpType.EQ);
        using Mat meanMat = new Mat();
        using Mat stdDevMat = new Mat();
        Cv2.MeanStdDev(workingMat, meanMat, stdDevMat, tempMask);
        double mean = meanMat.At<double>(0, 0);
        double sigma = stdDevMat.At<double>(0, 0);
        
        double threshold = mean + (sigma * 3.0);
        int minArea = 8; // Filtro area minima

        // 4. Binarizzazione
        using Mat maskF = new Mat();
        Cv2.Threshold(workingMat, maskF, threshold, 1.0, ThresholdTypes.Binary);
        using Mat mask8U = new Mat();
        maskF.ConvertTo(mask8U, MatType.CV_8UC1, 255.0);

        // 5. Analisi Blob (Connected Components)
        using Mat labels = new Mat();
        using Mat stats = new Mat();
        using Mat centroids = new Mat();
        int numFeatures = Cv2.ConnectedComponentsWithStats(mask8U, labels, stats, centroids);

        if (numFeatures <= 1) 
            return new Point2D(workingMat.Width / 2.0, workingMat.Height / 2.0);

        // 6. Trova il blob migliore (Area maggiore e >= minArea)
        int bestLabel = -1, bestMaxArea = -1;
        for (int i = 1; i < numFeatures; i++)
        {
            int area = stats.At<int>(i, (int)ConnectedComponentsTypes.Area);
            if (area >= minArea && area > bestMaxArea) 
            { 
                bestMaxArea = area; 
                bestLabel = i; 
            }
        }

        if (bestLabel == -1) 
            return new Point2D(workingMat.Width / 2.0, workingMat.Height / 2.0);

        // 7. CRUCIALE: Estrai SOLO la ROI attorno al blob (Evita loop su 1MPx)
        int bX = stats.At<int>(bestLabel, (int)ConnectedComponentsTypes.Left);
        int bY = stats.At<int>(bestLabel, (int)ConnectedComponentsTypes.Top);
        int bW = stats.At<int>(bestLabel, (int)ConnectedComponentsTypes.Width);
        int bH = stats.At<int>(bestLabel, (int)ConnectedComponentsTypes.Height);
        
        // Padding di sicurezza
        int pX = Math.Max(0, bX - 2);
        int pY = Math.Max(0, bY - 2);
        int pW = Math.Min(workingMat.Cols - pX, bW + 4);
        int pH = Math.Min(workingMat.Rows - pY, bH + 4);

        Rect2D starRect = new Rect2D(pX, pY, pW, pH);
        using Mat starCrop = new Mat(workingMat, new Rect(pX, pY, pW, pH));
        
        Point2D centerInStarCrop;
        try 
        { 
            // 8. Esegui Fit Gaussiano sul crop (Veloce)
            centerInStarCrop = FindGaussianCenter(starCrop); 
        }
        catch 
        { 
            centerInStarCrop = new Point2D(starRect.Width / 2.0, starRect.Height / 2.0); 
        }

        // 9. Coordinate Globali
        return new Point2D(centerInStarCrop.X + starRect.X, centerInStarCrop.Y + starRect.Y);
    }

    public Point2D FindGaussianCenter(Mat rawMat, double sigma = 3.0)
    {
        if (rawMat == null || rawMat.Empty()) return new Point2D(-1, -1);

        using Mat workingMat = new Mat();
        if (rawMat.Type() != MatType.CV_64FC1) rawMat.ConvertTo(workingMat, MatType.CV_64FC1);
        else rawMat.CopyTo(workingMat);

        using Mat smoothedMat = new Mat();
        if (sigma > 0) Cv2.GaussianBlur(workingMat, smoothedMat, new Size(0, 0), sigma, sigma);
        else workingMat.CopyTo(smoothedMat);

        Cv2.MinMaxLoc(smoothedMat, out double minVal, out double maxVal);
        
        if ((maxVal - minVal) <= 1e-6) return FindPeak(smoothedMat, sigma: 0);

        // Preparazione dati ottimizzata: usa FindNonZero invece di ciclare tutto
        double scaleFactor = 1.0 / (maxVal - minVal);
        double threshold = minVal + ((maxVal - minVal) * 0.2); 

        using Mat maskF = new Mat();
        Cv2.Threshold(smoothedMat, maskF, threshold, 1.0, ThresholdTypes.Binary);
        using Mat mask8U = new Mat();
        maskF.ConvertTo(mask8U, MatType.CV_8UC1);
        
        using Mat locationsMat = new Mat();
        Cv2.FindNonZero(mask8U, locationsMat); 
        
        if (locationsMat.Total() < 6) return FindPeak(smoothedMat, sigma: 0); 
        
        locationsMat.GetArray(out Point[] locations);

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
                double amp = p[0], xo = p[1], yo = p[2];
                double sX = Math.Max(Math.Abs(p[3]), 1e-6); 
                double sY = Math.Max(Math.Abs(p[4]), 1e-6);
                double off = p[5];
                
                var model = Vector<double>.Build.Dense(xData.Length, i =>
                {
                    double dx = (xData[i][0] - xo);
                    double dy = (xData[i][1] - yo);
                    return off + amp * Math.Exp(-0.5 * ((dx * dx) / (sX * sX) + (dy * dy) / (sY * sY)));
                });
                
                return (yDataVector - model).L2Norm();
            };
            
            var result = FindMinimum.OfFunctionConstrained(objectiveFunc, lowerBounds, upperBounds, initialGuess, maxIterations: 500);
            return new Point2D(result[1], result[2]);
        }
        catch 
        { 
            return FindPeak(smoothedMat, sigma: 0); 
        }
    }

    public Point2D FindPeak(Mat rawMat, double sigma = 1.0)
    {
        if (rawMat == null || rawMat.Empty()) return new Point2D(-1, -1);
        
        using Mat workingMat = new Mat();
        if (rawMat.Type() != MatType.CV_64FC1) rawMat.ConvertTo(workingMat, MatType.CV_64FC1);
        else rawMat.CopyTo(workingMat);
        
        using Mat blurredMat = new Mat();
        if (sigma > 0) Cv2.GaussianBlur(workingMat, blurredMat, new Size(0, 0), sigma, sigma);
        else workingMat.CopyTo(blurredMat);
        
        Cv2.MinMaxLoc(blurredMat, out _, out _, out _, out Point maxLoc);
        int x0 = maxLoc.X, y0 = maxLoc.Y;
        
        // Logica OLD: Interpolazione quadratica per sub-pixel accuracy
        if (y0 > 0 && x0 > 0 && y0 < blurredMat.Rows - 1 && x0 < blurredMat.Cols - 1)
        {
            try
            {
                var idx = blurredMat.GetGenericIndexer<double>();
                double c = idx[y0, x0];
                double r = idx[y0, x0 + 1], l = idx[y0, x0 - 1];
                double u = idx[y0 - 1, x0], d = idx[y0 + 1, x0];
                
                double dxx = r + l - 2 * c;
                double dyy = d + u - 2 * c;
                
                double subX = x0, subY = y0;
                if (Math.Abs(dxx) >= 1e-9) subX = x0 - ((r - l) / (2.0 * dxx));
                if (Math.Abs(dyy) >= 1e-9) subY = y0 - ((d - u) / (2.0 * dyy));
                
                return new Point2D(subX, subY);
            }
            catch { }
        }
        return new Point2D(x0, y0);
    }

    public Point2D FindCentroid(Mat rawMat, double sigma = 5.0)
    {
        if (rawMat == null || rawMat.Empty()) return new Point2D(-1, -1);
        
        using Mat workingMat = new Mat();
        if (rawMat.Type() != MatType.CV_64FC1) rawMat.ConvertTo(workingMat, MatType.CV_64FC1);
        else rawMat.CopyTo(workingMat);
        
        using Mat blurredMat = new Mat();
        if (sigma > 0) 
        { 
            int k = (int)(sigma * 3) | 1; 
            Cv2.GaussianBlur(workingMat, blurredMat, new Size(k, k), sigma); 
        }
        else workingMat.CopyTo(blurredMat);
        
        Cv2.Normalize(blurredMat, blurredMat, 0, 1, NormTypes.MinMax);
        
        Moments moments = Cv2.Moments(blurredMat, binaryImage: false);
        if (moments.M00 != 0) 
            return new Point2D(moments.M10 / moments.M00, moments.M01 / moments.M00);
        
        return new Point2D(blurredMat.Width / 2.0, blurredMat.Height / 2.0);
    }

    // =======================================================================
    // 3. REGISTRAZIONE & SHIFT (FFT + SOBEL) - LOGICA OLD
    // =======================================================================

    public (Point2D Shift, double Confidence) ComputeStarFieldShift(Mat reference, Mat target)
    {
        if (reference == null || target == null || reference.Empty() || target.Empty()) 
            return (new Point2D(0, 0), 0);

        // Usa Sobel filter (Logica Old robusta)
        using var refEdge = ApplySobelFilter(reference);
        using var tgtEdge = ApplySobelFilter(target);

        int rows = 3;
        int cols = 3;
        
        List<Point2d> shifts = new List<Point2d>(rows * cols);
        int cellW = refEdge.Width / cols;
        int cellH = refEdge.Height / rows;

        using var hanningWin = new Mat();
        Cv2.CreateHanningWindow(hanningWin, new Size(cellW, cellH), MatType.CV_32FC1);

        using var cellRef32 = new Mat();
        using var cellTgt32 = new Mat();

        // Griglia 3x3 per robustezza
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                int x = c * cellW;
                int y = r * cellH;
                if (x + cellW > refEdge.Width || y + cellH > refEdge.Height) continue;

                var rect = new Rect(x, y, cellW, cellH);
                using var cellRef = new Mat(refEdge, rect);
                using var cellTgt = new Mat(tgtEdge, rect);
                
                if (cellRef.Type() != MatType.CV_32FC1) cellRef.ConvertTo(cellRef32, MatType.CV_32FC1); else cellRef.CopyTo(cellRef32);
                if (cellTgt.Type() != MatType.CV_32FC1) cellTgt.ConvertTo(cellTgt32, MatType.CV_32FC1); else cellTgt.CopyTo(cellTgt32);
                
                Cv2.MinMaxLoc(cellRef32, out _, out double maxVal);
                // Salta celle vuote/rumorose
                if (maxVal < 0.01) continue; 

                Point2d shift = Cv2.PhaseCorrelate(cellRef32, cellTgt32, hanningWin, out double response);
                
                if (response > 0.05 && Math.Abs(shift.X) < cellW / 2.0)
                {
                    shifts.Add(shift);
                }
            }
        }

        if (shifts.Count == 0) return (new Point2D(0, 0), 0.0);

        Point2d finalShift = ComputeConsensusShift(shifts);
        return (new Point2D(finalShift.X, finalShift.Y), 1.0); 
    }

    public Point2D? FindTemplatePosition(Mat searchImage, Mat template, Point2D expectedCenter, int searchRadius)
    {
        // Porting della logica da ImageOperationService (Old)
        if (searchImage.Empty() || template.Empty()) return null;

        // 1. Ritaglio area di ricerca 
        int sx = (int)Math.Max(0, expectedCenter.X - searchRadius);
        int sy = (int)Math.Max(0, expectedCenter.Y - searchRadius);
        int sw = (int)Math.Min(searchRadius * 2, searchImage.Width - sx);
        int sh = (int)Math.Min(searchRadius * 2, searchImage.Height - sy);

        if (sw <= template.Width || sh <= template.Height) return null;

        using Mat searchRegion = new Mat(searchImage, new Rect(sx, sy, sw, sh));
        using Mat res = new Mat();
        
        // 2. Template Matching
        Cv2.MatchTemplate(searchRegion, template, res, TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(res, out _, out double maxVal, out _, out Point maxLoc);

        if (maxVal < 0.4) return null; // Tolleranza leggermente aumentata

        double matchCx = maxLoc.X + (template.Width / 2.0);
        double matchCy = maxLoc.Y + (template.Height / 2.0);
        Point2D roughLocal = new Point2D(matchCx, matchCy);

        // 3. Raffinamento Sub-Pixel (usando FindCenterOfLocalRegion su crop interno)
        // Questo è ciò che mancava nella "New Version"
        int refineRad = searchRadius / 2;
        int rsx = (int)Math.Max(0, roughLocal.X - refineRad);
        int rsy = (int)Math.Max(0, roughLocal.Y - refineRad);
        int rsw = (int)Math.Min(refineRad * 2, searchRegion.Width - rsx);
        int rsh = (int)Math.Min(refineRad * 2, searchRegion.Height - rsy);

        Point2D subPixelCenter;
        if (rsw > 4 && rsh > 4)
        {
            using var refineCrop = new Mat(searchRegion, new Rect(rsx, rsy, rsw, rsh));
            var localRefined = FindCenterOfLocalRegion(refineCrop);
            subPixelCenter = new Point2D(localRefined.X + rsx, localRefined.Y + rsy);
        }
        else
        {
            subPixelCenter = roughLocal;
        }

        return new Point2D(subPixelCenter.X + sx, subPixelCenter.Y + sy);
    }

    // =======================================================================
    // HELPER PRIVATI (LOGICA OLD)
    // =======================================================================

    private Mat ApplySobelFilter(Mat input)
    {
        Mat grayFloat = new Mat();
        
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

        Cv2.Normalize(grayFloat, grayFloat, 0, 1, NormTypes.MinMax);

        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(15, 15));
        using var topHat = new Mat();
        Cv2.MorphologyEx(grayFloat, topHat, MorphTypes.TopHat, kernel);

        using var blurred = new Mat();
        Cv2.GaussianBlur(topHat, blurred, new Size(0, 0), 1.5);

        using var gradX = new Mat();
        using var gradY = new Mat();
        Cv2.Sobel(blurred, gradX, MatType.CV_32FC1, 1, 0, ksize: 3);
        Cv2.Sobel(blurred, gradY, MatType.CV_32FC1, 0, 1, ksize: 3);

        var magnitude = new Mat();
        Cv2.Magnitude(gradX, gradY, magnitude);

        using var meanMat = new Mat();
        using var stdDevMat = new Mat();
        Cv2.MeanStdDev(magnitude, meanMat, stdDevMat);
        double mean = meanMat.Get<double>(0, 0);
        double std = stdDevMat.Get<double>(0, 0);
        
        Cv2.Threshold(magnitude, magnitude, mean + (2.0 * std), 0, ThresholdTypes.Tozero);

        return magnitude;
    }

    private Point2d ComputeConsensusShift(List<Point2d> points)
    {
        if (points.Count == 0) return new Point2d(0, 0);
        if (points.Count == 1) return points[0];

        List<Point2d> bestCluster = new List<Point2d>();
        double tolerance = 2.0; 

        foreach (var p1 in points)
        {
            var currentCluster = new List<Point2d>();
            foreach (var p2 in points)
            {
                double dist = Math.Sqrt(Math.Pow(p1.X - p2.X, 2) + Math.Pow(p1.Y - p2.Y, 2));
                if (dist <= tolerance) currentCluster.Add(p2);
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