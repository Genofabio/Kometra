using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices; 
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;
using Kometra.Models.Fits;
using Kometra.Services.Fits.Conversion;

namespace Kometra.Services.Processing.Engines;

public class InpaintingEngine : IInpaintingEngine
{
    private readonly IFitsOpenCvConverter _converter;

    // Configurazione
    private const int HaloDilationSize = 8;
    private const int InitialWindow = 15;
    private const int MaxWindow = 151;
    private const int StepWindow = 15;
    private const int MaxIter = 30;

    public InpaintingEngine(IFitsOpenCvConverter converter)
    {
        _converter = converter ?? throw new ArgumentNullException(nameof(converter));
    }

    public Mat InpaintStars(Mat image, Mat starMask, Mat cometMask = null)
    {
        if (!image.IsContinuous()) image = image.Clone();

        // 1. PREPARAZIONE MASCHERE (OpenCV)
        using Mat nanMask = new Mat();
        Cv2.Compare(image, image, nanMask, CmpTypes.NE); 

        using Mat fillMask = new Mat();
        if (starMask.Type() != MatType.CV_8UC1) starMask.ConvertTo(fillMask, MatType.CV_8UC1);
        else starMask.CopyTo(fillMask);
        Cv2.BitwiseOr(fillMask, nanMask, fillMask);

        using Mat safeMask = new Mat();
        using Mat dilatedStars = new Mat();
        using Mat kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, 
            new Size(2 * HaloDilationSize + 1, 2 * HaloDilationSize + 1));
        
        Cv2.Dilate(fillMask, dilatedStars, kernel);
        Cv2.BitwiseOr(dilatedStars, nanMask, safeMask); 

        // INTEGRAZIONE MASCHERA COMETA
        if (cometMask != null)
        {
            using Mat comet8U = new Mat();
            if (cometMask.Type() != MatType.CV_8UC1) cometMask.ConvertTo(comet8U, MatType.CV_8UC1);
            else cometMask.CopyTo(comet8U);
            
            // Non campionare dalla cometa
            Cv2.BitwiseOr(safeMask, comet8U, safeMask);

            // Impediamo di modificare i pixel che appartengono alla cometa
            using Mat notComet = new Mat();
            Cv2.BitwiseNot(comet8U, notComet);
            Cv2.BitwiseAnd(fillMask, notComet, fillMask); 
        }

        Cv2.BitwiseNot(safeMask, safeMask); // 255 = Safe, 0 = Bad

        int w = image.Cols;
        int h = image.Rows;
        int totalPixels = w * h;

        byte[] fillFlat = new byte[totalPixels];
        Marshal.Copy(fillMask.Data, fillFlat, 0, totalPixels);

        byte[] safeFlat = new byte[totalPixels];
        Marshal.Copy(safeMask.Data, safeFlat, 0, totalPixels);

        // 3. ELABORAZIONE
        if (image.Type() == MatType.CV_32FC1)
        {
            float[] imgFlat = new float[totalPixels];
            Marshal.Copy(image.Data, imgFlat, 0, totalPixels);
            
            ProcessGridFloat(imgFlat, fillFlat, safeFlat, w, h);
            
            Mat result = new Mat(h, w, MatType.CV_32FC1);
            Marshal.Copy(imgFlat, 0, result.Data, totalPixels);
            return result;
        }
        else if (image.Type() == MatType.CV_64FC1)
        {
            double[] imgFlat = new double[totalPixels];
            Marshal.Copy(image.Data, imgFlat, 0, totalPixels);
            
            ProcessGridDouble(imgFlat, fillFlat, safeFlat, w, h);
            
            Mat result = new Mat(h, w, MatType.CV_64FC1);
            Marshal.Copy(imgFlat, 0, result.Data, totalPixels);
            return result;
        }
        else
        {
            throw new NotSupportedException("Formato immagine non supportato.");
        }
    }

    private void ProcessGridFloat(float[] img, byte[] fill, byte[] safe, int w, int h)
    {
        var pixelsToFill = new List<int>(img.Length / 20);
        for (int i = 0; i < fill.Length; i++)
        {
            if (fill[i] > 0) pixelsToFill.Add(i);
        }

        int iteration = 0;
        int windowSize = InitialWindow;

        while (pixelsToFill.Count > 0 && iteration < MaxIter && windowSize <= MaxWindow)
        {
            int[] nextPixelsBuffer = new int[pixelsToFill.Count];
            int nextCount = 0;
            int rad = windowSize / 2;
            int currentMaxProbes = 60 + (windowSize * 2);

            Parallel.ForEach(pixelsToFill, idx =>
            {
                int py = idx / w;
                int px = idx % w;

                int count = 0;
                double mean = 0.0;
                double M2 = 0.0;
                float localMin = float.MaxValue;
                float localMax = float.MinValue;
                
                int yMin = py - rad;
                int yMax = py + rad;
                int xMin = px - rad;
                int xMax = px + rad;
                
                if (xMin < 0) { xMax += (0 - xMin); xMin = 0; } 
                else if (xMax >= w) { xMin -= (xMax - (w - 1)); xMax = w - 1; }
                
                if (yMin < 0) { yMax += (0 - yMin); yMin = 0; } 
                else if (yMax >= h) { yMin -= (yMax - (h - 1)); yMax = h - 1; }
                
                xMin = Math.Max(0, xMin); xMax = Math.Min(w - 1, xMax);
                yMin = Math.Max(0, yMin); yMax = Math.Min(h - 1, yMax);
                int widthRange = xMax - xMin + 1;
                int heightRange = yMax - yMin + 1;

                uint seed = (uint)(px * 73856093 ^ py * 19349663 ^ iteration * 83492791 + 2166136261);

                for (int k = 0; k < currentMaxProbes && count < 15; k++)
                {
                    seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5;
                    int ry = yMin + (int)(seed % (uint)heightRange);
                    seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5;
                    int rx = xMin + (int)(seed % (uint)widthRange);

                    int neighborIdx = ry * w + rx;

                    if (safe[neighborIdx] == 255)
                    {
                        float val = img[neighborIdx];
                        if (!float.IsNaN(val))
                        {
                            count++;
                            double delta = val - mean;
                            mean += delta / count;
                            M2 += delta * (val - mean);
                            
                            if (val < localMin) localMin = val;
                            if (val > localMax) localMax = val;
                        }
                    }
                }

                if (count >= 8)
                {
                    double variance = M2 / count;
                    float noise = GenerateGaussianNoiseFloat((float)mean, (float)Math.Sqrt(variance), ref seed);

                    if (float.IsNaN(noise) || float.IsInfinity(noise)) noise = (float)mean;
                    
                    // Clamp Dinamico Locale per evitare pixel bianchi/neri
                    if (noise < localMin) noise = localMin;
                    if (noise > localMax) noise = localMax;

                    img[idx] = noise;
                }
                else
                {
                    int storeIdx = Interlocked.Increment(ref nextCount) - 1;
                    nextPixelsBuffer[storeIdx] = idx;
                }
            });

            var nextIterList = new List<int>(nextCount);
            for(int k=0; k<nextCount; k++) nextIterList.Add(nextPixelsBuffer[k]);
            pixelsToFill = nextIterList;

            if (pixelsToFill.Count > 0) windowSize = Math.Min(windowSize + StepWindow, MaxWindow);
            iteration++;
        }

        foreach(var idx in pixelsToFill) img[idx] = 0.0f;
    }

    private void ProcessGridDouble(double[] img, byte[] fill, byte[] safe, int w, int h)
    {
        var pixelsToFill = new List<int>(img.Length / 20);
        for (int i = 0; i < fill.Length; i++)
        {
            if (fill[i] > 0) pixelsToFill.Add(i);
        }

        int iteration = 0;
        int windowSize = InitialWindow;

        while (pixelsToFill.Count > 0 && iteration < MaxIter && windowSize <= MaxWindow)
        {
            int[] nextPixelsBuffer = new int[pixelsToFill.Count];
            int nextCount = 0;
            int rad = windowSize / 2;
            int currentMaxProbes = 60 + (windowSize * 2);

            Parallel.ForEach(pixelsToFill, idx =>
            {
                int py = idx / w;
                int px = idx % w;

                int count = 0;
                double mean = 0.0;
                double M2 = 0.0;
                double localMin = double.MaxValue;
                double localMax = double.MinValue;

                int yMin = py - rad;
                int yMax = py + rad;
                int xMin = px - rad;
                int xMax = px + rad;
                
                if (xMin < 0) { xMax += (0 - xMin); xMin = 0; } 
                else if (xMax >= w) { xMin -= (xMax - (w - 1)); xMax = w - 1; }
                
                if (yMin < 0) { yMax += (0 - yMin); yMin = 0; } 
                else if (yMax >= h) { yMin -= (yMax - (h - 1)); yMax = h - 1; }
                
                xMin = Math.Max(0, xMin); xMax = Math.Min(w - 1, xMax);
                yMin = Math.Max(0, yMin); yMax = Math.Min(h - 1, yMax);
                int widthRange = xMax - xMin + 1;
                int heightRange = yMax - yMin + 1;

                uint seed = (uint)(px * 73856093 ^ py * 19349663 ^ iteration * 83492791 + 2166136261);

                for (int k = 0; k < currentMaxProbes && count < 15; k++)
                {
                    seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5;
                    int ry = yMin + (int)(seed % (uint)heightRange);
                    seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5;
                    int rx = xMin + (int)(seed % (uint)widthRange);

                    int neighborIdx = ry * w + rx;

                    if (safe[neighborIdx] == 255)
                    {
                        double val = img[neighborIdx];
                        if (!double.IsNaN(val))
                        {
                            count++;
                            double delta = val - mean;
                            mean += delta / count;
                            M2 += delta * (val - mean);
                            
                            if (val < localMin) localMin = val;
                            if (val > localMax) localMax = val;
                        }
                    }
                }

                if (count >= 8)
                {
                    double variance = M2 / count;
                    double noise = GenerateGaussianNoiseDouble(mean, Math.Sqrt(variance), ref seed);

                    if (double.IsNaN(noise) || double.IsInfinity(noise)) noise = mean;
                    
                    if (noise < localMin) noise = localMin;
                    if (noise > localMax) noise = localMax;

                    img[idx] = noise;
                }
                else
                {
                    int storeIdx = Interlocked.Increment(ref nextCount) - 1;
                    nextPixelsBuffer[storeIdx] = idx;
                }
            });

            var nextIterList = new List<int>(nextCount);
            for(int k=0; k<nextCount; k++) nextIterList.Add(nextPixelsBuffer[k]);
            pixelsToFill = nextIterList;

            if (pixelsToFill.Count > 0) windowSize = Math.Min(windowSize + StepWindow, MaxWindow);
            iteration++;
        }

        foreach(var idx in pixelsToFill) img[idx] = 0.0;
    }

    private float GenerateGaussianNoiseFloat(float mean, float stdDev, ref uint seed)
    {
        double u1 = NextDouble(ref seed);
        double u2 = NextDouble(ref seed);
        if (u1 < 1e-9) u1 = 1e-9;
        double randStdNormal = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
        return mean + stdDev * (float)randStdNormal;
    }

    private double GenerateGaussianNoiseDouble(double mean, double stdDev, ref uint seed)
    {
        double u1 = NextDouble(ref seed);
        double u2 = NextDouble(ref seed);
        if (u1 < 1e-9) u1 = 1e-9;
        double randStdNormal = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
        return mean + stdDev * randStdNormal;
    }

    private double NextDouble(ref uint seed)
    {
        seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5;
        return seed * 2.3283064365386963e-10;
    }
}