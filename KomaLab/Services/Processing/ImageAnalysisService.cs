using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using KomaLab.Models.Primitives;
using MathNet.Numerics;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.Statistics;
using OpenCvSharp;

namespace KomaLab.Services.Processing;

// ---------------------------------------------------------------------------
// FILE: ImageAnalysisService.cs
// RUOLO: Compute Engine (Matematica Pura)
// DESCRIZIONE:
// Motore di calcolo "stateless" per l'analisi delle immagini.
// Non ha dipendenze da I/O o UI. Prende matrici di pixel e restituisce numeri.
//
// FUNZIONI CHIAVE:
// 1. Centroiding: Trova il centro esatto di stelle/comete con precisione sub-pixel
//    usando Gaussian Fitting 2D (MathNet) o Momenti (OpenCV).
// 2. Alignment: Calcola lo spostamento tra due immagini usando Phase Correlation (FFT).
// 3. Statistics: Calcola istogrammi e livelli per l'AutoStretch visuale.
// ---------------------------------------------------------------------------

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
        
        // Maschera per ignorare NaN (comuni dopo rotazioni/allineamenti)
        Cv2.Compare(image, image, mask, CmpType.EQ);
        Cv2.MeanStdDev(image, meanMat, stdDevMat, mask);
        
        double mean = meanMat.Get<double>(0, 0);
        double std = stdDevMat.Get<double>(0, 0);
        
        // Protezione contro valori degeneri
        if (double.IsNaN(mean)) mean = 0;
        if (double.IsNaN(std) || std < 1e-9) std = 1.0;
        
        return (mean, std);
    }
    
    public Rect2D FindValidDataBox(Mat image)
    {
        // Trova il rettangolo che contiene pixel validi (non neri/nulli)
        using Mat mask = new Mat();
        Cv2.Compare(image, image, mask, CmpType.EQ); // NaN check implicito
        
        // Se l'immagine ha bordi neri (0), possiamo raffinarla con Threshold > 0
        Cv2.Threshold(image, mask, 0, 255, ThresholdTypes.Binary);
        mask.ConvertTo(mask, MatType.CV_8UC1);

        var rect = Cv2.BoundingRect(mask);
        return new Rect2D(rect.X, rect.Y, rect.Width, rect.Height);
    }

    // =======================================================================
    // 2. CENTRAMENTO LOCALE (Workflow Automatico)
    // =======================================================================

    public Point2D FindCenterOfLocalRegion(Mat regionMat)
    {
        if (regionMat.Empty()) return new Point2D(0, 0);
        
        // Normalizzazione a Double per precisione
        using Mat workingMat = new Mat();
        if (regionMat.Type() != MatType.CV_64FC1) regionMat.ConvertTo(workingMat, MatType.CV_64FC1);
        else regionMat.CopyTo(workingMat);

        // 1. Pre-pass: Gaussian Blur per ridurre il rumore e trovare il picco globale "vero"
        using Mat statsMat = new Mat();
        Cv2.GaussianBlur(workingMat, statsMat, new Size(3, 3), 0);
        Cv2.MinMaxLoc(statsMat, out double minVal, out double maxVal);
        
        if ((maxVal - minVal) <= 1e-6) return new Point2D(workingMat.Width / 2.0, workingMat.Height / 2.0);

        // 2. Blob Detection (Connected Components)
        // Utile per distinguere la stella/cometa da raggi cosmici o hot pixel isolati
        var (mean, sigma) = ComputeStatistics(workingMat);
        double threshold = mean + (sigma * 3.0);
        int minArea = 8; // Filtra rumore puntiforme

        using Mat maskF = new Mat();
        Cv2.Threshold(workingMat, maskF, threshold, 1.0, ThresholdTypes.Binary);
        using Mat mask8U = new Mat();
        maskF.ConvertTo(mask8U, MatType.CV_8UC1, 255.0);

        using Mat labels = new Mat();
        using Mat stats = new Mat();
        using Mat centroids = new Mat();
        int numFeatures = Cv2.ConnectedComponentsWithStats(mask8U, labels, stats, centroids);

        if (numFeatures <= 1) return new Point2D(workingMat.Width / 2.0, workingMat.Height / 2.0);

        // Trova il blob più grande (escluso background label 0)
        int bestLabel = -1, maxArea = -1;
        for (int i = 1; i < numFeatures; i++)
        {
            int area = stats.At<int>(i, (int)ConnectedComponentsTypes.Area);
            if (area >= minArea && area > maxArea) { maxArea = area; bestLabel = i; }
        }

        if (bestLabel == -1) return new Point2D(workingMat.Width / 2.0, workingMat.Height / 2.0);

        // 3. Crop stretto sul blob vincente per il fitting finale
        int bX = stats.At<int>(bestLabel, (int)ConnectedComponentsTypes.Left);
        int bY = stats.At<int>(bestLabel, (int)ConnectedComponentsTypes.Top);
        int bW = stats.At<int>(bestLabel, (int)ConnectedComponentsTypes.Width);
        int bH = stats.At<int>(bestLabel, (int)ConnectedComponentsTypes.Height);
        
        // Aggiungiamo un margine (padding) per il fitting
        int pX = Math.Max(0, bX - 2);
        int pY = Math.Max(0, bY - 2);
        int pW = Math.Min(workingMat.Cols - pX, bW + 4);
        int pH = Math.Min(workingMat.Rows - pY, bH + 4);

        Rect2D starRect = new Rect2D(pX, pY, pW, pH);
        using Mat starCrop = new Mat(workingMat, new Rect(pX, pY, pW, pH));
        
        // 4. Fitting Gaussiano di precisione
        Point2D centerInStarCrop;
        try 
        { 
            centerInStarCrop = FindGaussianCenter(starCrop); 
        }
        catch 
        { 
            // Fallback geometrico se il fit fallisce
            centerInStarCrop = new Point2D(starRect.Width / 2.0, starRect.Height / 2.0); 
        }

        // Coordinate relative -> globali
        return new Point2D(centerInStarCrop.X + starRect.X, centerInStarCrop.Y + starRect.Y);
    }

    // =======================================================================
    // 3. ALGORITMI CORE (GAUSSIAN FITTING / PEAK / MOMENTS)
    // =======================================================================

    public Point2D FindGaussianCenter(Mat rawMat, double sigma = 3.0)
    {
        if (rawMat.Empty()) return new Point2D(-1, -1);
        
        using Mat workingMat = new Mat();
        if (rawMat.Type() != MatType.CV_64FC1) rawMat.ConvertTo(workingMat, MatType.CV_64FC1);
        else rawMat.CopyTo(workingMat);

        using Mat smoothedMat = new Mat();
        if (sigma > 0) Cv2.GaussianBlur(workingMat, smoothedMat, new Size(0, 0), sigma, sigma);
        else workingMat.CopyTo(smoothedMat);
        
        Cv2.MinMaxLoc(smoothedMat, out double minVal, out double maxVal);
        
        if ((maxVal - minVal) <= 1e-6) return FindPeak(smoothedMat, sigma: 0);

        // Estrazione punti significativi per il fitting (riduce complessità calcolo)
        // Prendiamo solo i punti sopra il 20% della luminosità relativa
        double scaleFactor = 1.0 / (maxVal - minVal);
        double threshold = minVal + ((maxVal - minVal) * 0.2); 

        using Mat maskF = new Mat();
        Cv2.Threshold(smoothedMat, maskF, threshold, 1.0, ThresholdTypes.Binary);
        using Mat mask8U = new Mat();
        maskF.ConvertTo(mask8U, MatType.CV_8UC1);
        
        using Mat locationsMat = new Mat();
        Cv2.FindNonZero(mask8U, locationsMat); // Ottimizzazione: lista sparsa di punti
        
        if (locationsMat.Total() < 6) return FindPeak(smoothedMat, sigma: 0); // Servono min 6 punti per fit 2D (6 parametri)
        
        locationsMat.GetArray(out Point[] locations);

        // Preparazione dati per MathNet
        List<double[]> xDataList = new List<double[]>();
        List<double> yDataList = new List<double>();
        var indexer = smoothedMat.GetGenericIndexer<double>();
        
        // Calcolo baricentro pesato per Initial Guess (fondamentale per convergenza rapida)
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
        
        // Vettore Parametri: [Amplitude, X0, Y0, SigmaX, SigmaY, Offset]
        var initialGuess = Vector<double>.Build.Dense([
            1.0, 
            xoInitNum / weightSum, 
            yoInitNum / weightSum, 
            3.0, 
            3.0, 
            0.0
        ]);
        
        // Vincoli fisici per evitare soluzioni impossibili (es. Sigma negativo o centro fuori immagine)
        var lowerBounds = Vector<double>.Build.Dense([0.0, 0, 0, 0.1, 0.1, -0.2]);
        var upperBounds = Vector<double>.Build.Dense([1.5, smoothedMat.Width, smoothedMat.Height, smoothedMat.Width, smoothedMat.Height, 0.5]);

        try
        {
            var yDataVector = Vector<double>.Build.Dense(yData);
            
            // Funzione Obiettivo: Gaussian 2D
            // f(x,y) = Offset + Amp * exp( -0.5 * ( ((x-x0)/sx)^2 + ((y-y0)/sy)^2 ) )
            Func<Vector<double>, double> objectiveFunc = (p) =>
            {
                double amp = p[0], xo = p[1], yo = p[2];
                double sX = Math.Max(Math.Abs(p[3]), 1e-6); // Evita divisione per zero
                double sY = Math.Max(Math.Abs(p[4]), 1e-6);
                double off = p[5];
                
                var model = Vector<double>.Build.Dense(xData.Length, i =>
                {
                    double dx = (xData[i][0] - xo);
                    double dy = (xData[i][1] - yo);
                    return off + amp * Math.Exp(-0.5 * ((dx * dx) / (sX * sX) + (dy * dy) / (sY * sY)));
                });
                
                // Residuo L2 (Minimi Quadrati)
                return (yDataVector - model).L2Norm();
            };
            
            // Solver non lineare
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
        // Metodo veloce: Max Location + Interpolazione Parabolica 3x3
        if (rawMat.Empty()) return new Point2D(-1, -1);
        using Mat workingMat = new Mat();
        if (rawMat.Type() != MatType.CV_64FC1) rawMat.ConvertTo(workingMat, MatType.CV_64FC1);
        else rawMat.CopyTo(workingMat);
        
        using Mat blurredMat = new Mat();
        if (sigma > 0) Cv2.GaussianBlur(workingMat, blurredMat, new Size(0, 0), sigma, sigma);
        else workingMat.CopyTo(blurredMat);
        
        Cv2.MinMaxLoc(blurredMat, out _, out _, out _, out Point maxLoc);
        int x0 = maxLoc.X, y0 = maxLoc.Y;
        
        // Sub-pixel refinement
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
                // Vertice parabola: x - b/2a
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
        // Metodo classico: Centroide pesato (Moments)
        if (rawMat.Empty()) return new Point2D(-1, -1);
        
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
    // 4. ANALISI SPOSTAMENTO CAMPO STELLARE (PHASE CORRELATION)
    // =======================================================================
    
    public Point2D ComputeStarFieldShift(Mat reference, Mat target)
    {
        if (reference.Empty() || target.Empty()) return new Point2D(0, 0);

        // 1. Edge Detection (Sobel) per rendere l'algoritmo insensibile ai gradienti di luminosità
        using var refEdge = ApplySobelFilter(reference);
        using var tgtEdge = ApplySobelFilter(target);

        // 2. Griglia di Correlazione (Divide et Impera)
        // Invece di una FFT globale (lenta e sensibile al rumore), dividiamo in 9 settori
        // e calcoliamo lo shift per ognuno.
        int rows = 3;
        int cols = 3;
        
        List<Point2d> shifts = new List<Point2d>(rows * cols);
        int cellW = refEdge.Width / cols;
        int cellH = refEdge.Height / rows;

        using var hanningWin = new Mat();
        Cv2.CreateHanningWindow(hanningWin, new Size(cellW, cellH), MatType.CV_32FC1);

        using var cellRef32 = new Mat();
        using var cellTgt32 = new Mat();

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
                
                // Conversione float per FFT
                if (cellRef.Type() != MatType.CV_32FC1) cellRef.ConvertTo(cellRef32, MatType.CV_32FC1); else cellRef.CopyTo(cellRef32);
                if (cellTgt.Type() != MatType.CV_32FC1) cellTgt.ConvertTo(cellTgt32, MatType.CV_32FC1); else cellTgt.CopyTo(cellTgt32);

                // Skip celle senza stelle (bassa deviazione standard o basso max value)
                Cv2.MinMaxLoc(cellRef32, out _, out double maxVal);
                if (maxVal < 0.1) continue; 

                // Phase Correlation
                Point2d shift = Cv2.PhaseCorrelate(cellRef32, cellTgt32, hanningWin, out double response);
                
                // Filtra risultati con bassa confidenza
                if (response > 0.05 && Math.Abs(shift.X) < cellW / 2.0)
                {
                    shifts.Add(shift);
                }
            }
        }

        if (shifts.Count == 0) return new Point2D(0, 0);

        // 3. Consensus: Trova lo shift "modale" (più frequente) per scartare outlier
        Point2d finalShift = ComputeConsensusShift(shifts);

        return new Point2D(finalShift.X, finalShift.Y); 
    }
    
    public (double Black, double White) CalculateAutoStretchLevels(Mat image)
    {
        if (image.Empty()) return (0, 1);
        Mat workingMat = image;
        bool disposeMat = false;

        // Assicurati Double
        if (image.Type() != MatType.CV_64FC1)
        {
            workingMat = new Mat();
            image.ConvertTo(workingMat, MatType.CV_64FC1);
            disposeMat = true;
        }

        try
        {
            // Ottimizzazione Statistica: Campionamento Sparse
            // Leggere 60MPixel richiede tempo. Leggerne 10.000 sparsi dà lo stesso istogramma.
            int maxSamples = 10000;
            long totalPixels = workingMat.Total();
            var samples = new List<double>(Math.Min((int)totalPixels, maxSamples));
            
            int width = workingMat.Width;
            int height = workingMat.Height;
            
            // Scansione "a righe sparse" per massimizzare throughput memoria
            int rowsToScan = (int)Math.Sqrt(maxSamples); 
            int rowStep = Math.Max(1, height / rowsToScan);

            for (int y = 0; y < height; y += rowStep)
            {
                double[] rowData = new double[width];
                Marshal.Copy(workingMat.Ptr(y), rowData, 0, width);

                int colStep = Math.Max(1, width / rowsToScan);
                for (int x = 0; x < width; x += colStep)
                {
                    double val = rowData[x];
                    if (!double.IsNaN(val) && !double.IsInfinity(val))
                    {
                        samples.Add(val);
                    }
                }
            }

            if (samples.Count == 0) return (0, 1);

            // Calcolo Quantili (5% - 99.5%)
            double b = Statistics.Quantile(samples, 0.05);
            double w = Statistics.Quantile(samples, 0.995);

            if (Math.Abs(w - b) < 1e-6) w = b + 1.0;

            return (b, w);
        }
        finally
        {
            if (disposeMat) workingMat.Dispose();
        }
    }

    // --- HELPER PRIVATI ---

    private Mat ApplySobelFilter(Mat input)
    {
        // Prepara l'immagine per la correlazione di fase estraendo solo i bordi/dettagli
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

        Cv2.Normalize(grayFloat, grayFloat, 0, 1, NormTypes.MinMax);

        // Rimuove gradienti sfondo (inquinamento luminoso)
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(15, 15));
        using var topHat = new Mat();
        Cv2.MorphologyEx(grayFloat, topHat, MorphTypes.TopHat, kernel);

        using var blurred = new Mat();
        Cv2.GaussianBlur(topHat, blurred, new Size(0, 0), 1.5);

        // Estrae gradienti (stelle diventano "ciambelle" o picchi netti)
        using var gradX = new Mat();
        using var gradY = new Mat();
        Cv2.Sobel(blurred, gradX, MatType.CV_32FC1, 1, 0, ksize: 3);
        Cv2.Sobel(blurred, gradY, MatType.CV_32FC1, 0, 1, ksize: 3);

        var magnitude = new Mat();
        Cv2.Magnitude(gradX, gradY, magnitude);

        // Pulisce rumore di fondo
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
        // Clustering semplice: trova il gruppo di vettori più numeroso che sono vicini tra loro
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

        // Media dei vettori nel cluster vincente
        double sumX = 0, sumY = 0;
        foreach (var p in bestCluster)
        {
            sumX += p.X;
            sumY += p.Y;
        }

        return new Point2d(sumX / bestCluster.Count, sumY / bestCluster.Count);
    }
}