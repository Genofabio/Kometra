using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices; // Necessario per Marshal.Copy
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
    private const int HaloDilationSize = 4;
    private const int InitialWindow = 15;
    private const int MaxWindow = 151;
    private const int StepWindow = 15;
    private const int MaxIter = 30;

    public InpaintingEngine(IFitsOpenCvConverter converter)
    {
        _converter = converter ?? throw new ArgumentNullException(nameof(converter));
    }

    public Mat InpaintStars(Mat image, Mat starMask)
    {
        // Assicuriamoci che l'immagine sia continua in memoria per usare Marshal.Copy
        // Se è una ROI (Region of Interest), Clone() la rende continua.
        if (!image.IsContinuous()) image = image.Clone();

        // 1. PREPARAZIONE MASCHERE (OpenCV)
        using Mat nanMask = new Mat();
        Cv2.Compare(image, image, nanMask, CmpType.NE); 

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
        Cv2.BitwiseNot(safeMask, safeMask); // 255 = Safe, 0 = Bad

        // 2. CONVERSIONE FLAT (1D Arrays) - Safe & Veloce con Marshal.Copy
        int w = image.Cols;
        int h = image.Rows;
        int totalPixels = w * h;

        // Copia Maschere (Byte)
        byte[] fillFlat = new byte[totalPixels];
        Marshal.Copy(fillMask.Data, fillFlat, 0, totalPixels);

        byte[] safeFlat = new byte[totalPixels];
        Marshal.Copy(safeMask.Data, safeFlat, 0, totalPixels);

        // 3. ELABORAZIONE
        // Usiamo Marshal.Copy per copiare IntPtr -> Array Managed
        // E poi Array Managed -> IntPtr. Non serve 'unsafe'.
        
        if (image.Type() == MatType.CV_32FC1)
        {
            float[] imgFlat = new float[totalPixels];
            Marshal.Copy(image.Data, imgFlat, 0, totalPixels); // Leggi
            
            ProcessGridFloat(imgFlat, fillFlat, safeFlat, w, h);
            
            // Scrivi risultato su una NUOVA matrice per evitare di modificare l'originale se è condivisa
            Mat result = new Mat(h, w, MatType.CV_32FC1);
            Marshal.Copy(imgFlat, 0, result.Data, totalPixels); // Scrivi
            return result;
        }
        else if (image.Type() == MatType.CV_64FC1)
        {
            double[] imgFlat = new double[totalPixels];
            Marshal.Copy(image.Data, imgFlat, 0, totalPixels); // Leggi
            
            ProcessGridDouble(imgFlat, fillFlat, safeFlat, w, h);
            
            Mat result = new Mat(h, w, MatType.CV_64FC1);
            Marshal.Copy(imgFlat, 0, result.Data, totalPixels); // Scrivi
            return result;
        }
        else
        {
            throw new NotSupportedException("Formato immagine non supportato (serve Float o Double).");
        }
    }

    // -------------------------------------------------------------------------------
    // IMPLEMENTAZIONE FLOAT (Array 1D)
    // -------------------------------------------------------------------------------
    private void ProcessGridFloat(float[] img, byte[] fill, byte[] safe, int w, int h)
    {
        // Lista indici 1D
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
            const int TargetSamples = 15;
            const int MaxProbes = 60;

            Parallel.ForEach(pixelsToFill, idx =>
            {
                int py = idx / w;
                int px = idx % w;

                int count = 0;
                double mean = 0.0;
                double M2 = 0.0;

                int yMin = Math.Max(0, py - rad);
                int yMax = Math.Min(h - 1, py + rad);
                int xMin = Math.Max(0, px - rad);
                int xMax = Math.Min(w - 1, px + rad);
                int widthRange = xMax - xMin + 1;
                int heightRange = yMax - yMin + 1;

                uint seed = (uint)(px * 31 + py * 17 + iteration * 13);

                for (int k = 0; k < MaxProbes && count < TargetSamples; k++)
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
                            double delta2 = val - mean;
                            M2 += delta * delta2;
                        }
                    }
                }

                if (count >= 8)
                {
                    double variance = M2 / count;
                    double stdDev = Math.Sqrt(variance);
                    float noise = GenerateGaussianNoiseFloat((float)mean, (float)stdDev, ref seed);

                    if (float.IsNaN(noise) || float.IsInfinity(noise)) noise = 0.0f;
                    if (noise < 0) noise = 0.0f;

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

    // -------------------------------------------------------------------------------
    // IMPLEMENTAZIONE DOUBLE (Array 1D)
    // -------------------------------------------------------------------------------
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
            const int TargetSamples = 15;
            const int MaxProbes = 60;

            Parallel.ForEach(pixelsToFill, idx =>
            {
                int py = idx / w;
                int px = idx % w;

                int count = 0;
                double mean = 0.0;
                double M2 = 0.0;

                int yMin = Math.Max(0, py - rad);
                int yMax = Math.Min(h - 1, py + rad);
                int xMin = Math.Max(0, px - rad);
                int xMax = Math.Min(w - 1, px + rad);
                int widthRange = xMax - xMin + 1;
                int heightRange = yMax - yMin + 1;

                uint seed = (uint)(px * 31 + py * 17 + iteration * 13);

                for (int k = 0; k < MaxProbes && count < TargetSamples; k++)
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
                            double delta2 = val - mean;
                            M2 += delta * delta2;
                        }
                    }
                }

                if (count >= 8)
                {
                    double variance = M2 / count;
                    double stdDev = Math.Sqrt(variance);
                    double noise = GenerateGaussianNoiseDouble(mean, stdDev, ref seed);

                    if (double.IsNaN(noise) || double.IsInfinity(noise)) noise = 0.0;
                    if (noise < 0) noise = 0.0;

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

    // --- RNG VELOCE (Zero Lock) ---

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