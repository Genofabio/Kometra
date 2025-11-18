using KomaLab.Models;
using MathNet.Numerics;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.Statistics;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using nom.tam.fits;
using Point = Avalonia.Point;

namespace KomaLab.Services;

/// <summary>
/// Implementazione del servizio di calcolo scientifico.
/// Gestisce Image Processing (OpenCV) e Fitting Matematico (MathNet).
/// </summary>
public class ImageProcessingService : IImageProcessingService
{
    // =======================================================================
    // 1. WORKFLOW INTELLIGENTI (High Level Processing)
    // =======================================================================
    
    public Point GetCenterOfLocalRegion(Mat regionMat, double thresholdRatio = 0.1, int minArea = 10, int padding = 2)
    {
        if (regionMat.Empty()) return new Point(0, 0);
        
        double medianVal;
        var matType = regionMat.Type();

        if (matType == MatType.CV_32FC1)
        {
            regionMat.GetArray(out float[] raw);
            var valid = raw.Select(v => (double)v).Where(v => !double.IsNaN(v) && !double.IsInfinity(v));
            var data = valid as double[] ?? valid.ToArray();
            medianVal = data.Any() ? Statistics.Median(data) : 0.0;
        }
        else
        {
            regionMat.GetArray(out double[] raw);
            var valid = raw.Where(v => !double.IsNaN(v) && !double.IsInfinity(v));
            var enumerable = valid as double[] ?? valid.ToArray();
            medianVal = enumerable.Any() ? Statistics.Median(enumerable) : 0.0;
        }

        if (medianVal <= 1e-6) medianVal = 1.0;
        double threshold = medianVal * (1 + thresholdRatio);
        
        using Mat maskF = new Mat();
        Cv2.Threshold(regionMat, maskF, threshold, 1.0, ThresholdTypes.Binary);
        
        using Mat mask8U = new Mat();
        maskF.ConvertTo(mask8U, MatType.CV_8UC1, 255.0);

        using Mat labels = new Mat();
        using Mat stats = new Mat(); 
        using Mat centroids = new Mat();
        int numFeatures = Cv2.ConnectedComponentsWithStats(mask8U, labels, stats, centroids);

        if (numFeatures <= 1) return new Point(regionMat.Width / 2.0, regionMat.Height / 2.0);
        
        int bestLabel = -1;
        int maxArea = -1;
        
        for (int i = 1; i < numFeatures; i++)
        {
            int area = stats.At<int>(i, (int)ConnectedComponentsTypes.Area);
            if (area >= minArea && area > maxArea)
            {
                maxArea = area;
                bestLabel = i;
            }
        }

        if (bestLabel == -1) return new Point(regionMat.Width / 2.0, regionMat.Height / 2.0);
        
        int bX = stats.At<int>(bestLabel, (int)ConnectedComponentsTypes.Left);
        int bY = stats.At<int>(bestLabel, (int)ConnectedComponentsTypes.Top);
        int bW = stats.At<int>(bestLabel, (int)ConnectedComponentsTypes.Width);
        int bH = stats.At<int>(bestLabel, (int)ConnectedComponentsTypes.Height);
        
        int paddingX = Math.Max(2, (int)(bW * 0.25));
        int paddingY = Math.Max(2, (int)(bH * 0.25));

        int pX = Math.Max(bX - paddingX, 0);
        int pY = Math.Max(bY - paddingY, 0);
        int pW = Math.Min(bX + bW + paddingX, regionMat.Cols) - pX;
        int pH = Math.Min(bY + bH + paddingY, regionMat.Rows) - pY;

        Rect starRect = new Rect(pX, pY, pW, pH);
        using Mat starCrop = new Mat(regionMat, starRect);
        
        Point centerInStarCrop;
        try
        {
            centerInStarCrop = GetCenterByGaussianFit(starCrop);
        }
        catch 
        {
            centerInStarCrop = new Point(starRect.Width / 2.0, starRect.Height / 2.0);
        }

        return new Point(centerInStarCrop.X + starRect.X, centerInStarCrop.Y + starRect.Y);
    }
    
    // =======================================================================
    // 2. ALGORITMI DI CENTRAGGIO (Math Core)
    // =======================================================================
    
    public Point GetCenterByGaussianFit(Mat rawMat, double sigma = 3.0)
    {
        if (rawMat.Empty()) return new Point(-1, -1);

        // 1. Pre-processing (Blur)
        using Mat smoothedMat = new Mat();
        if (sigma > 0)
            Cv2.GaussianBlur(rawMat, smoothedMat, new Size(0, 0), sigma, sigma);
        else
            rawMat.CopyTo(smoothedMat);

        // 2. Soglia Automatica basata sulla Dinamica (FWHM)
        Cv2.MinMaxLoc(smoothedMat, out double minVal, out double maxVal);
        double dynamicRange = maxVal - minVal;

        if (dynamicRange <= 1e-6) 
            return GetCenterByPeak(smoothedMat, sigma: 0); // Fallback immediato su immagine piatta

        double threshold = minVal + (dynamicRange * 0.5); // Half Maximum

        // 3. Estrazione Punti per il Fit
        using Mat maskF = new Mat();
        Cv2.Threshold(smoothedMat, maskF, threshold, 1.0, ThresholdTypes.Binary);

        List<double[]> xDataList = [];
        List<double> yDataList = [];
        var matType = smoothedMat.Type();

        // Uso Indexer per velocità
        if (matType == MatType.CV_32FC1)
        {
            var indexer = smoothedMat.GetGenericIndexer<float>();
            var maskIdx = maskF.GetGenericIndexer<float>();
            for (int y = 0; y < smoothedMat.Rows; y++) {
                for (int x = 0; x < smoothedMat.Cols; x++) {
                    if (maskIdx[y, x] > 0.5f) {
                        xDataList.Add([x, y]);
                        yDataList.Add(indexer[y, x]);
                    }
                }
            }
        }
        else
        {
            var indexer = smoothedMat.GetGenericIndexer<double>();
            var maskIdx = maskF.GetGenericIndexer<float>();
            for (int y = 0; y < smoothedMat.Rows; y++) {
                for (int x = 0; x < smoothedMat.Cols; x++) {
                    if (maskIdx[y, x] > 0.5f) {
                        xDataList.Add([x, y]);
                        yDataList.Add(indexer[y, x]);
                    }
                }
            }
        }

        if (xDataList.Count < 6) return GetCenterByPeak(smoothedMat, sigma: 0); // Pochi punti

        // 4. Setup MathNet Fit
        double[][] xData = xDataList.ToArray();
        double[] yData = yDataList.ToArray();
        
        // Stima iniziale parametri
        double xoInitNum = 0, yoInitNum = 0, weightSum = 0;
        for(int i = 0; i < yData.Length; i++)
        {
            double w = Math.Max(0, yData[i] - minVal); // Peso senza fondo
            xoInitNum += xData[i][0] * w;
            yoInitNum += xData[i][1] * w;
            weightSum += w;
        }
        if (weightSum <= 1e-9) return GetCenterByPeak(smoothedMat, sigma: 0);

        var initialGuess = Vector<double>.Build.Dense([
            dynamicRange,           // Ampiezza
            xoInitNum / weightSum,  // X0
            yoInitNum / weightSum,  // Y0
            3.0,                    // Sigma X
            3.0,                    // Sigma Y
            minVal                  // Offset
        ]);
        
        var lowerBounds = Vector<double>.Build.Dense([0, 0, 0, 0.1, 0.1, -double.MaxValue]);
        var upperBounds = Vector<double>.Build.Dense([double.MaxValue, smoothedMat.Width, smoothedMat.Height, smoothedMat.Width, smoothedMat.Height, double.MaxValue]);

        try
        {
            // Definizione Funzione Obiettivo WLS (Weighted Least Squares)
            double minSig = yData.Where(y => y > 0).DefaultIfEmpty(1.0).Min();
            var weightsVector = Vector<double>.Build.Dense(yData.Length, i => 1.0 / Math.Sqrt(Math.Max(Math.Abs(yData[i]), minSig)));
            var yDataVector = Vector<double>.Build.Dense(yData);

            Func<Vector<double>, double> objectiveFunc = (p) =>
            {
                double amp = p[0], xo = p[1], yo = p[2], sX = Math.Max(Math.Abs(p[3]), 1e-6), sY = Math.Max(Math.Abs(p[4]), 1e-6), off = p[5];
                var model = Vector<double>.Build.Dense(xData.Length, i => 
                {
                    double dx = (xData[i][0] - xo);
                    double dy = (xData[i][1] - yo);
                    return off + amp * Math.Exp(-0.5 * ((dx*dx)/(sX*sX) + (dy*dy)/(sY*sY)));
                });
                return (yDataVector - model).PointwiseMultiply(weightsVector).L2Norm();
            };

            var result = FindMinimum.OfFunctionConstrained(objectiveFunc, lowerBounds, upperBounds, initialGuess, maxIterations: 100);
            
            if (double.IsNaN(result[1]) || double.IsNaN(result[2])) throw new Exception("NaN Fit");
            return new Point(result[1], result[2]);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"GaussianFit Fallback: {ex.Message}");
            // Fallback: Peak su immagine mascherata
            using Mat mask8U = new Mat();
            maskF.ConvertTo(mask8U, MatType.CV_8UC1, 255.0);
            using Mat masked = Mat.Zeros(smoothedMat.Size(), smoothedMat.Type());
            smoothedMat.CopyTo(masked, mask8U);
            return GetCenterByPeak(masked, sigma: 0);
        }
    }

    public Point GetCenterByPeak(Mat rawMat, double sigma = 1.0) 
    {
        if (rawMat.Empty()) return new Point(-1, -1);
        
        using Mat workingMat = new Mat();
        if (sigma > 0)
            Cv2.GaussianBlur(rawMat, workingMat, new Size(0, 0), sigma, sigma);
        else
            rawMat.CopyTo(workingMat);
        
        Cv2.MinMaxLoc(workingMat, out _, out _, out _, out OpenCvSharp.Point maxLoc);
        int x0 = maxLoc.X;
        int y0 = maxLoc.Y;

        // Check bordi
        if (y0 <= 0 || x0 <= 0 || y0 >= workingMat.Rows - 1 || x0 >= workingMat.Cols - 1)
            return new Point(x0, y0); 
        
        // Fit Parabolico
        try
        {
            double subX = x0, subY = y0;
            
            // Usa Indexer per gestire sia Float che Double
            if (workingMat.Type() == MatType.CV_32FC1)
            {
                var idx = workingMat.GetGenericIndexer<float>();
                float c = idx[y0, x0], r = idx[y0, x0+1], l = idx[y0, x0-1], u = idx[y0-1, x0], d = idx[y0+1, x0];
                double dxx = r + l - 2*c;
                double dyy = d + u - 2*c;
                if (Math.Abs(dxx) >= 1e-6) subX = x0 - ((r - l) / 2.0) / dxx;
                if (Math.Abs(dyy) >= 1e-6) subY = y0 - ((d - u) / 2.0) / dyy;
            }
            else
            {
                var idx = workingMat.GetGenericIndexer<double>();
                double c = idx[y0, x0], r = idx[y0, x0+1], l = idx[y0, x0-1], u = idx[y0-1, x0], d = idx[y0+1, x0];
                double dxx = r + l - 2*c;
                double dyy = d + u - 2*c;
                if (Math.Abs(dxx) >= 1e-6) subX = x0 - ((r - l) / 2.0) / dxx;
                if (Math.Abs(dyy) >= 1e-6) subY = y0 - ((d - u) / 2.0) / dyy;
            }
            return new Point(subX, subY);
        }
        catch
        {
            return new Point(x0, y0);
        }
    }

    public Point GetCenterByCentroid(Mat rawMat, double sigma = 5.0)
    {
        if (rawMat.Empty()) return new Point(-1, -1);
        
        using Mat workingMat = new Mat();
        if (sigma > 0)
            Cv2.GaussianBlur(rawMat, workingMat, new Size(0, 0), sigma, sigma);
        else
            rawMat.CopyTo(workingMat);

        Moments moments = Cv2.Moments(workingMat, binaryImage: false);

        if (moments.M00 != 0)
            return new Point(moments.M10 / moments.M00, moments.M01 / moments.M00);
    
        return new Point(workingMat.Width / 2.0, workingMat.Height / 2.0);
    }
    

    // =======================================================================
    // 3. TRASFORMAZIONI GEOMETRICHE (Warp & Shift)
    // =======================================================================

    public Mat GetSubPixelCenteredCanvas(Mat source, Point originalCenter, Size outputSize)
    {
        double destCenterX = outputSize.Width / 2.0;
        double destCenterY = outputSize.Height / 2.0;
        
        double tx = destCenterX - originalCenter.X;
        double ty = destCenterY - originalCenter.Y;
        
        using Mat m = new Mat(2, 3, MatType.CV_32F);
        m.Set(0, 0, 1.0f); m.Set(0, 1, 0.0f); m.Set(0, 2, (float)tx);
        m.Set(1, 0, 0.0f); m.Set(1, 1, 1.0f); m.Set(1, 2, (float)ty);
        
        Mat result = new Mat(outputSize, source.Type(), new Scalar(double.NaN));
        
        Cv2.WarpAffine(
            source, 
            result, 
            m, 
            outputSize, 
            InterpolationFlags.Linear, 
            BorderTypes.Transparent, 
            new Scalar(double.NaN)
        );

        return result;
    }
    
    public Rect FindValidDataBox(Mat imageMat)
    {
        using Mat nanMask = new Mat();
        Cv2.Compare(imageMat, imageMat, nanMask, CmpType.EQ);
        Rect validDataBox = Cv2.BoundingRect(nanMask);

        if (validDataBox.Width <= 0 || validDataBox.Height <= 0) return new Rect();
        return validDataBox;
    }

    // =======================================================================
    // 4. FITS I/O & CONVERSIONE (Bridge)
    // =======================================================================

    public Mat LoadFitsDataAsMat(FitsImageData fitsData)
    {
        var rawJaggedData = (Array[])fitsData.RawData;
        int width = (int)fitsData.ImageSize.Width;
        int height = (int)fitsData.ImageSize.Height;
        int bitpix = fitsData.FitsHeader.GetIntValue("BITPIX");

        Mat imageMat;

        switch (bitpix)
        {
            case -32: // Float (32-bit floating point)
                float[] flatDataF = ConvertJaggedToFlat<float>(rawJaggedData, width, height);
                imageMat = new Mat(height, width, MatType.CV_32FC1);
                imageMat.SetArray(flatDataF);
                break;
            
            case -64: // Double (64-bit floating point)
                double[] flatDataD = ConvertJaggedToFlat<double>(rawJaggedData, width, height);
                imageMat = new Mat(height, width, MatType.CV_64FC1);
                imageMat.SetArray(flatDataD);
                break;
        
            case 16: // Short (16-bit integer)
                short[] flatDataS = ConvertJaggedToFlat<short>(rawJaggedData, width, height);
                imageMat = new Mat(height, width, MatType.CV_16SC1);
                imageMat.SetArray(flatDataS);
                break;

            case 8: // Byte (8-bit integer)
                byte[] flatDataB = ConvertJaggedToFlat<byte>(rawJaggedData, width, height);
                imageMat = new Mat(height, width, MatType.CV_8UC1);
                imageMat.SetArray(flatDataB);
                break;

            case 32: // Int (32-bit integer)
                int[] flatDataI = ConvertJaggedToFlat<int>(rawJaggedData, width, height);
                imageMat = new Mat(height, width, MatType.CV_32SC1);
                imageMat.SetArray(flatDataI);
                break;

            default:
                Debug.WriteLine($"LoadFitsDataAsMat: BITPIX non supportato {bitpix}");
                imageMat = new Mat();
                break;
        }
    
        return imageMat;
    }
    
    public FitsImageData CreateFitsDataFromMat(Mat mat, FitsImageData originalData)
    {
        int width = mat.Width;
        int height = mat.Height;
        Array[] jaggedData = new Array[height];

        if (mat.Type() == MatType.CV_32FC1)
        {
            mat.GetArray(out float[] f);
            for (int j = 0; j < height; j++) {
                jaggedData[j] = new float[width];
                Array.Copy(f, j * width, jaggedData[j], 0, width);
            }
        }
        else // CV_64FC1
        {
            mat.GetArray(out double[] d);
            for (int j = 0; j < height; j++) {
                jaggedData[j] = new double[width];
                Array.Copy(d, j * width, jaggedData[j], 0, width);
            }
        }
        
        var newHeader = new Header();
        foreach (object item in originalData.FitsHeader)
        {
            if (item is System.Collections.DictionaryEntry entry && entry.Value is HeaderCard card)
                newHeader.AddCard(card);
        }
        
        return new FitsImageData
        {
            RawData = jaggedData,
            FitsHeader = newHeader,
            ImageSize = new Avalonia.Size(width, height)
        };
    }

    public (double BlackPoint, double WhitePoint) CalculateClippedThresholds(FitsImageData fitsData)
    {
        int bitpix = fitsData.FitsHeader.GetIntValue("BITPIX");
        var jagged = (Array[])fitsData.RawData;

        return bitpix switch
        {
            8 => GetPercentiles(ConvertJaggedArray<byte>(jagged)),
            16 => GetPercentiles(ConvertJaggedArray<short>(jagged)),
            32 => GetPercentiles(ConvertJaggedArray<int>(jagged)),
            -32 => GetPercentiles(ConvertJaggedArray<float>(jagged)),
            -64 => GetPercentiles(ConvertJaggedArray<double>(jagged)),
            _ => (0, 255)
        };
    }
    
    // --- Helpers Privati ---

    private (double, double) GetPercentiles<T>(T[][] data) where T : struct, IConvertible
    {
        if (data.Length == 0) return (0, 255);
        // Linq generico lento ma funzionale per display
        var values = data.SelectMany(row => row.Select(p => Convert.ToDouble(p))
                         .Where(v => !double.IsNaN(v) && !double.IsInfinity(v) && v != 0));
        return CalculateQuantiles(values);
    }
    
    private (double, double) CalculateQuantiles(IEnumerable<double> pixelValues)
    {
        var list = pixelValues.ToList();
        if (list.Count == 0) return (0, 255);
        
        double b = Statistics.Quantile(list, 0.02);
        double w = Statistics.Quantile(list, 0.998);
        return (w <= b) ? (list.Min(), list.Max()) : (b, w);
    }

    private T[][] ConvertJaggedArray<T>(Array[] source) where T : struct
    {
        T[][] result = new T[source.Length][];
        for (int i = 0; i < source.Length; i++) result[i] = (T[])source[i];
        return result;
    }
    
    private T[] ConvertJaggedToFlat<T>(Array[] jaggedArray, int width, int height) where T : struct
    {
        T[] flatArray = new T[width * height];
        int k = 0;
        for (int j = 0; j < height; j++)
        {
            Array.Copy((T[])jaggedArray[j], 0, flatArray, k, width);
            k += width;
        }
        return flatArray;
    }
}