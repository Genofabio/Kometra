using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using OpenCvSharp;
using KomaLab.Models.Fits;
using KomaLab.Models.Fits.Structure;
using KomaLab.Services.Fits.Conversion; 

namespace KomaLab.Services.Processing.Engines;

public class InpaintingEngine : IInpaintingEngine
{
    private readonly IFitsOpenCvConverter _converter;

    // Configurazione
    private const int HaloDilationSize = 4; // Raggio di dilatazione della maschera (definisce l'area Safe)
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
        // ---------------------------------------------------------
        // 1. PREPARAZIONE MASCHERE (OpenCV)
        // ---------------------------------------------------------
        
        // A. Maschera NaN
        using Mat nanMask = new Mat();
        Cv2.Compare(image, image, nanMask, CmpType.NE); 

        // B. Maschera Fill (Stelle + NaN)
        using Mat fillMask = new Mat();
        if (starMask.Type() != MatType.CV_8UC1) starMask.ConvertTo(fillMask, MatType.CV_8UC1);
        else starMask.CopyTo(fillMask);
        Cv2.BitwiseOr(fillMask, nanMask, fillMask);

        // C. Maschera Safe (Dove leggere)
        // Usiamo la dilatazione per escludere geometricamente gli aloni.
        // Non avremo bisogno di controllare la distanza pixel per pixel nel loop.
        using Mat safeMask = new Mat();
        using Mat dilatedStars = new Mat();
        using Mat kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, 
            new Size(2 * HaloDilationSize + 1, 2 * HaloDilationSize + 1));
        
        // Dilatazione: Espande la zona "cattiva" (Stelle + Aloni)
        Cv2.Dilate(fillMask, dilatedStars, kernel);
        
        // Uniamo Aloni e NaN nella lista "cattivi"
        Cv2.BitwiseOr(dilatedStars, nanMask, safeMask); 
        
        // Invertiamo: 255 = Safe (Cielo Vero e Pulito), 0 = Bad (Stella, Alone o NaN)
        Cv2.BitwiseNot(safeMask, safeMask); 

        // ---------------------------------------------------------
        // 2. TRASFERIMENTO DATI IN RAM
        // ---------------------------------------------------------
        byte[,] fillGrid = (byte[,])_converter.MatToRaw(fillMask, FitsBitDepth.UInt8);
        byte[,] safeGrid = (byte[,])_converter.MatToRaw(safeMask, FitsBitDepth.UInt8);

        if (image.Type() == MatType.CV_32FC1)
        {
            float[,] imgGrid = (float[,])_converter.MatToRaw(image, FitsBitDepth.Float);
            ProcessGridFloat(imgGrid, fillGrid, safeGrid);
            return _converter.RawToMat(imgGrid, 1.0, 0.0, FitsBitDepth.Float);
        }
        else if (image.Type() == MatType.CV_64FC1)
        {
            double[,] imgGrid = (double[,])_converter.MatToRaw(image, FitsBitDepth.Double);
            ProcessGridDouble(imgGrid, fillGrid, safeGrid);
            return _converter.RawToMat(imgGrid, 1.0, 0.0, FitsBitDepth.Double);
        }
        else
        {
            throw new NotSupportedException("Serve Float o Double.");
        }
    }

    // -------------------------------------------------------------------------------
    // IMPLEMENTAZIONE FLOAT (Welford / Zero Alloc / Solo SafeMask check)
    // -------------------------------------------------------------------------------
    private void ProcessGridFloat(float[,] img, byte[,] fill, byte[,] safe)
    {
        int h = img.GetLength(0);
        int w = img.GetLength(1);

        var pixelsToFill = new List<Point>(w * h / 20);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (fill[y, x] > 0) pixelsToFill.Add(new Point(x, y));

        int iteration = 0;
        int windowSize = InitialWindow;

        while (pixelsToFill.Count > 0 && iteration < MaxIter && windowSize <= MaxWindow)
        {
            var nextIterPixels = new ConcurrentBag<Point>();
            int rad = windowSize / 2;
            const int TargetSamples = 15;
            const int MaxProbes = 60;

            Parallel.ForEach(pixelsToFill, pt =>
            {
                int px = pt.X;
                int py = pt.Y;

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
                    // RNG Veloce
                    seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5;
                    int ry = yMin + (int)(seed % (uint)heightRange);
                    seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5;
                    int rx = xMin + (int)(seed % (uint)widthRange);

                    // RIMOSSO: if (Math.Abs(...) <= Halo) -> Non serve più!
                    // La maschera 'safe' è già dilatata. 
                    // Se safe[ry, rx] è 255, siamo matematicamente sicuri di essere fuori dall'alone.

                    if (safe[ry, rx] == 255)
                    {
                        float val = img[ry, rx];
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

                    // Protezione finale
                    if (float.IsNaN(noise) || float.IsInfinity(noise)) noise = 0.0f;
                    if (noise < 0) noise = 0.0f;

                    img[py, px] = noise;
                }
                else
                {
                    nextIterPixels.Add(pt);
                }
            });

            var remainingSet = new HashSet<Point>(nextIterPixels);
            foreach (var pt in pixelsToFill)
            {
                if (!remainingSet.Contains(pt)) fill[pt.Y, pt.X] = 0;
            }
            pixelsToFill = nextIterPixels.ToList();
            
            if (pixelsToFill.Count > 0) windowSize = Math.Min(windowSize + StepWindow, MaxWindow);
            iteration++;
        }

        foreach(var pt in pixelsToFill) img[pt.Y, pt.X] = 0.0f;
    }

    // -------------------------------------------------------------------------------
    // IMPLEMENTAZIONE DOUBLE
    // -------------------------------------------------------------------------------
    private void ProcessGridDouble(double[,] img, byte[,] fill, byte[,] safe)
    {
        int h = img.GetLength(0);
        int w = img.GetLength(1);

        var pixelsToFill = new List<Point>(w * h / 20);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (fill[y, x] > 0) pixelsToFill.Add(new Point(x, y));

        int iteration = 0;
        int windowSize = InitialWindow;

        while (pixelsToFill.Count > 0 && iteration < MaxIter && windowSize <= MaxWindow)
        {
            var nextIterPixels = new ConcurrentBag<Point>();
            int rad = windowSize / 2;
            const int TargetSamples = 15;
            const int MaxProbes = 60;

            Parallel.ForEach(pixelsToFill, pt =>
            {
                int px = pt.X;
                int py = pt.Y;

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

                    // Controllo solo sulla maschera dilatata
                    if (safe[ry, rx] == 255)
                    {
                        double val = img[ry, rx];
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

                    img[py, px] = noise;
                }
                else
                {
                    nextIterPixels.Add(pt);
                }
            });

            var remainingSet = new HashSet<Point>(nextIterPixels);
            foreach (var pt in pixelsToFill)
            {
                if (!remainingSet.Contains(pt)) fill[pt.Y, pt.X] = 0;
            }
            pixelsToFill = nextIterPixels.ToList();
            
            if (pixelsToFill.Count > 0) windowSize = Math.Min(windowSize + StepWindow, MaxWindow);
            iteration++;
        }

        foreach(var pt in pixelsToFill) img[pt.Y, pt.X] = 0.0;
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