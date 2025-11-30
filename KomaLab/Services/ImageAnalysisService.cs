using System;
using System.Collections.Generic;
using System.Diagnostics;
using MathNet.Numerics;
using MathNet.Numerics.LinearAlgebra;
using OpenCvSharp;
using Point = Avalonia.Point;
using Rect = Avalonia.Rect;

namespace KomaLab.Services;

public class ImageAnalysisService : IImageAnalysisService
{
    // =======================================================================
    // 1. STATISTICHE
    // =======================================================================

    public (double Mean, double StdDev) ComputeStatistics(Mat image)
    {
        if (image.Empty()) return (0, 1);

        using Mat meanMat = new Mat();
        using Mat stdDevMat = new Mat();
        using Mat mask = new Mat();

        // 1. Maschera per ignorare NaN (Logica identica al tuo ComputeStatistics)
        Cv2.Compare(image, image, mask, CmpType.EQ);
        
        // 2. Calcolo
        Cv2.MeanStdDev(image, meanMat, stdDevMat, mask);

        double mean = meanMat.Get<double>(0, 0);
        double std = stdDevMat.Get<double>(0, 0);

        if (double.IsNaN(mean)) mean = 0;
        if (double.IsNaN(std) || std < 1e-9) std = 1.0;

        return (mean, std);
    }

    // =======================================================================
    // 2. WORKFLOW INTELLIGENTI (High Level)
    // =======================================================================

    public Point FindCenterOfLocalRegion(Mat regionMat)
    {
        if (regionMat.Empty())
        {
            Debug.WriteLine("[LocalRegion] ERRORE: Matrice di input vuota.");
            return new Point(0, 0);
        }
        
        // Uso 'using' per la workingMat
        using Mat workingMat = new Mat();
        if (regionMat.Type() != MatType.CV_64FC1)
            regionMat.ConvertTo(workingMat, MatType.CV_64FC1);
        else
            regionMat.CopyTo(workingMat);

        // 1. Statistica Robusta (Logica identica)
        using Mat statsMat = new Mat();
        Cv2.GaussianBlur(workingMat, statsMat, new Size(3, 3), 0);
        Cv2.MinMaxLoc(statsMat, out double minVal, out double maxVal);

        double dynamicRange = maxVal - minVal;

        if (dynamicRange <= 1e-6) 
        {
            Debug.WriteLine("[LocalRegion] FALLBACK: Immagine piatta.");
            return new Point(workingMat.Width / 2.0, workingMat.Height / 2.0);
        }

        // 2. Parametri Dinamici (Uso il metodo interno che replica la tua logica MeanStdDev)
        var (mean, sigma) = ComputeStatistics(workingMat);
    
        double kSigma = 3.0; 
        double threshold = mean + (sigma * kSigma);
        int minArea = 8; // RIPRISTINATO A 8 COME NEL TUO CODICE

        // 3. Blob Detection
        using Mat maskF = new Mat();
        Cv2.Threshold(workingMat, maskF, threshold, 1.0, ThresholdTypes.Binary);
        
        using Mat mask8U = new Mat();
        maskF.ConvertTo(mask8U, MatType.CV_8UC1, 255.0);

        using Mat labels = new Mat();
        using Mat stats = new Mat(); 
        using Mat centroids = new Mat();
        int numFeatures = Cv2.ConnectedComponentsWithStats(mask8U, labels, stats, centroids);

        if (numFeatures <= 1) 
        {
            return new Point(workingMat.Width / 2.0, workingMat.Height / 2.0);
        }
        
        int bestLabel = -1;
        int maxArea = -1;
        for (int i = 1; i < numFeatures; i++)
        {
            int area = stats.At<int>(i, (int)ConnectedComponentsTypes.Area);
            if (area >= minArea && area > maxArea) { maxArea = area; bestLabel = i; }
        }

        if (bestLabel == -1) 
        {
            return new Point(workingMat.Width / 2.0, workingMat.Height / 2.0);
        }
        
        // 4. Crop e Padding (TUA LOGICA ESATTA ripristinata)
        int bX = stats.At<int>(bestLabel, (int)ConnectedComponentsTypes.Left);
        int bY = stats.At<int>(bestLabel, (int)ConnectedComponentsTypes.Top);
        int bW = stats.At<int>(bestLabel, (int)ConnectedComponentsTypes.Width);
        int bH = stats.At<int>(bestLabel, (int)ConnectedComponentsTypes.Height);
        
        int paddingX = Math.Max(2, (int)(bW * 0.25));
        int paddingY = Math.Max(2, (int)(bH * 0.25));
        int pX = Math.Max(bX - paddingX, 0);
        int pY = Math.Max(bY - paddingY, 0);
        int pW = Math.Min(bX + bW + paddingX, workingMat.Cols) - pX;
        int pH = Math.Min(bY + bH + paddingY, workingMat.Rows) - pY;

        Rect starRect = new Rect(pX, pY, pW, pH);
        
        // 'starCrop' avvolto in using
        using Mat starCrop = new Mat(workingMat, new OpenCvSharp.Rect(pX, pY, pW, pH));
        
        // 5. Calcolo
        Point centerInStarCrop;
        try
        {
            centerInStarCrop = FindGaussianCenter(starCrop);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LocalRegion] FALLBACK: {ex.Message}");
            centerInStarCrop = new Point(starRect.Width / 2.0, starRect.Height / 2.0);
        }

        return new Point(centerInStarCrop.X + starRect.X, centerInStarCrop.Y + starRect.Y);
    }

    // =======================================================================
    // 3. ALGORITMI CORE (Math Core)
    // =======================================================================

    public Point FindGaussianCenter(Mat rawMat, double sigma = 3.0)
    {
        if (rawMat.Empty()) return new Point(-1, -1);

        using Mat workingMat = new Mat();
        if (rawMat.Type() != MatType.CV_64FC1)
             rawMat.ConvertTo(workingMat, MatType.CV_64FC1);
        else
             rawMat.CopyTo(workingMat);

        using Mat smoothedMat = new Mat();
        if (sigma > 0)
            Cv2.GaussianBlur(workingMat, smoothedMat, new Size(0, 0), sigma, sigma);
        else
            workingMat.CopyTo(smoothedMat);
        
        Cv2.MinMaxLoc(smoothedMat, out double minVal, out double maxVal);
        double dynamicRange = maxVal - minVal;

        if (dynamicRange <= 1e-6) 
            return FindPeak(smoothedMat, sigma: 0); 

        double scaleFactor = 1.0 / dynamicRange;
        double threshold = minVal + (dynamicRange * 0.2); 

        using Mat maskF = new Mat();
        Cv2.Threshold(smoothedMat, maskF, threshold, 1.0, ThresholdTypes.Binary);
        
        using Mat mask8U = new Mat();
        maskF.ConvertTo(mask8U, MatType.CV_8UC1);
        
        using Mat locationsMat = new Mat();
        Cv2.FindNonZero(mask8U, locationsMat);
        
        if (locationsMat.Total() < 6) return FindPeak(smoothedMat, sigma: 0);
        
        locationsMat.GetArray(out OpenCvSharp.Point[] locations);

        // Logica MathNet (IDENTICA alla tua)
        List<double[]> xDataList = new List<double[]>(locations.Length);
        List<double> yDataList = new List<double>(locations.Length);
        
        var indexer = smoothedMat.GetGenericIndexer<double>();
        
        double xoInitNum = 0, yoInitNum = 0, weightSum = 0;

        foreach (var p in locations)
        {
            double rawVal = indexer[p.Y, p.X];
            double normVal = (rawVal - minVal) * scaleFactor;
            
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
        
        // Vettori MathNet (COPIATI DAL TUO CODICE)
        var initialGuess = Vector<double>.Build.Dense([
            1.0, xoInitNum / weightSum, yoInitNum / weightSum, 
            3.0, 3.0, 0.0
        ]);
        
        var lowerBounds = Vector<double>.Build.Dense([0.0, 0, 0, 0.1, 0.1, -0.2]);
        var upperBounds = Vector<double>.Build.Dense([1.5, smoothedMat.Width, smoothedMat.Height, smoothedMat.Width, smoothedMat.Height, 0.5]);

        try
        {
            Vector<double>.Build.Dense(yData.Length, 1.0); 
            var yDataVector = Vector<double>.Build.Dense(yData);

            // Objective Func (COPIATA DAL TUO CODICE)
            Func<Vector<double>, double> objectiveFunc = (p) =>
            {
                double amp = p[0], xo = p[1], yo = p[2], sX = Math.Max(Math.Abs(p[3]), 1e-6), sY = Math.Max(Math.Abs(p[4]), 1e-6), off = p[5];
                var model = Vector<double>.Build.Dense(xData.Length, i => 
                {
                    double dx = (xData[i][0] - xo);
                    double dy = (xData[i][1] - yo);
                    return off + amp * Math.Exp(-0.5 * ((dx*dx)/(sX*sX) + (dy*dy)/(sY*sY)));
                });
                return (yDataVector - model).L2Norm();
            };

            var result = FindMinimum.OfFunctionConstrained(objectiveFunc, lowerBounds, upperBounds, initialGuess, maxIterations: 1000);
            
            if (double.IsNaN(result[1]) || double.IsNaN(result[2])) throw new Exception("NaN Fit Result");
            
            return new Point(result[1], result[2]);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"GaussianFit Fallback: {ex.Message}");
            return FindPeak(smoothedMat, sigma: 0);
        }
    }

    public Point FindPeak(Mat rawMat, double sigma = 1.0)
    {
        if (rawMat.Empty()) return new Point(-1, -1);
        
        using Mat workingMat = new Mat();
        if (rawMat.Type() != MatType.CV_64FC1)
             rawMat.ConvertTo(workingMat, MatType.CV_64FC1);
        else
             rawMat.CopyTo(workingMat);
             
        using Mat blurredMat = new Mat();
        if (sigma > 0)
            Cv2.GaussianBlur(workingMat, blurredMat, new Size(0, 0), sigma, sigma);
        else
            workingMat.CopyTo(blurredMat);
        
        Cv2.MinMaxLoc(blurredMat, out _, out _, out _, out OpenCvSharp.Point maxLoc);
        int x0 = maxLoc.X;
        int y0 = maxLoc.Y;

        if (y0 <= 0 || x0 <= 0 || y0 >= blurredMat.Rows - 1 || x0 >= blurredMat.Cols - 1)
            return new Point(x0, y0); 
        
        try
        {
            // Formula Fit Parabolico (IDENTICA ALLA TUA)
            var idx = blurredMat.GetGenericIndexer<double>(); // Nota: Ho messo <double> perché workingMat è 64FC1
            double c = idx[y0, x0], r = idx[y0, x0+1], l = idx[y0, x0-1], u = idx[y0-1, x0], d = idx[y0+1, x0];
            double dxx = r + l - 2*c;
            double dyy = d + u - 2*c;
            double subX = x0, subY = y0;
            if (Math.Abs(dxx) >= 1e-6) subX = x0 - ((r - l) / 2.0) / dxx;
            if (Math.Abs(dyy) >= 1e-6) subY = y0 - ((d - u) / 2.0) / dyy;
            return new Point(subX, subY);
        }
        catch { return new Point(x0, y0); }
    }

    public Point FindCentroid(Mat rawMat, double sigma = 5.0)
    {
        if (rawMat.Empty()) return new Point(-1, -1);
        
        using Mat workingMat = new Mat();
        if (rawMat.Type() != MatType.CV_64FC1)
             rawMat.ConvertTo(workingMat, MatType.CV_64FC1);
        else
             rawMat.CopyTo(workingMat);

        using Mat blurredMat = new Mat();
        if (sigma > 0)
        {
            int k = (int)(sigma * 3) | 1;
            Cv2.GaussianBlur(workingMat, blurredMat, new Size(k, k), sigma);
        }
        else
            workingMat.CopyTo(blurredMat);

        // Questa riga c'era nel tuo codice originale, la mantengo
        Cv2.Normalize(blurredMat, blurredMat, 0, 1, NormTypes.MinMax);

        Moments moments = Cv2.Moments(blurredMat, binaryImage: false);

        if (moments.M00 != 0)
            return new Point(moments.M10 / moments.M00, moments.M01 / moments.M00);
    
        return new Point(blurredMat.Width / 2.0, blurredMat.Height / 2.0);
    }
    
    public Rect FindValidDataBox(Mat image)
    {
        using Mat mask = new Mat();
        // Check NaN (Logica tua)
        Cv2.Compare(image, image, mask, CmpType.EQ);
        var rect = Cv2.BoundingRect(mask);
        return new Rect(rect.X, rect.Y, rect.Width, rect.Height);
    }
}