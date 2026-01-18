using System;
using System.Collections.Generic;
using KomaLab.Models.Primitives;
using MathNet.Numerics;
using MathNet.Numerics.LinearAlgebra;
using OpenCvSharp;

namespace KomaLab.Services.Processing.Engines;

/// <summary>
/// Servizio di analisi geometrica e astrometrica.
/// Identifica posizioni, centri stellari e spostamenti tra immagini.
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
        // Identifica i pixel validi (non-NaN)
        Cv2.Compare(image, image, mask, CmpType.EQ); 
        var rect = Cv2.BoundingRect(mask);
        
        return new Rect2D(rect.X, rect.Y, rect.Width, rect.Height);
    }

    // =======================================================================
    // 2. CENTROIDING (IL CUORE DELL'ALLINEAMENTO)
    // =======================================================================

    public Point2D FindCenterOfLocalRegion(Mat region)
    {
        if (region == null || region.Empty()) return new Point2D(0, 0);

        // 1. Soglia adattiva basata sulla statistica locale
        using Mat workingMat = new Mat();
        region.ConvertTo(workingMat, MatType.CV_64FC1);
        
        Cv2.MeanStdDev(workingMat, out Scalar mean, out Scalar stddev);
        double threshold = mean.Val0 + (stddev.Val0 * 3.0);

        using Mat mask8U = new Mat();
        Cv2.Threshold(workingMat, mask8U, threshold, 255, ThresholdTypes.Binary);
        mask8U.ConvertTo(mask8U, MatType.CV_8UC1);

        // 2. Trova la componente connessa più significativa
        using Mat labels = new Mat();
        using Mat stats = new Mat();
        using Mat centroids = new Mat();
        int numFeatures = Cv2.ConnectedComponentsWithStats(mask8U, labels, stats, centroids);

        if (numFeatures <= 1) return new Point2D(workingMat.Width / 2.0, workingMat.Height / 2.0);

        // Assumiamo che la feature 1 sia l'oggetto più luminoso/centrale
        return FindGaussianCenter(workingMat, 3.0);
    }

    public Point2D FindGaussianCenter(Mat rawMat, double sigma = 3.0)
    {
        if (rawMat == null || rawMat.Empty()) return new Point2D(-1, -1);

        using Mat smoothed = new Mat();
        if (sigma > 0) Cv2.GaussianBlur(rawMat, smoothed, new Size(0, 0), sigma);
        else rawMat.CopyTo(smoothed);

        Cv2.MinMaxLoc(smoothed, out double minVal, out double maxVal, out _, out Point p);
        if ((maxVal - minVal) <= 1e-6) return new Point2D(p.X, p.Y);

        // Fit 2D Gaussian (Levenberg-Marquardt)
        var samples = new List<(double X, double Y, double Val)>();
        var indexer = rawMat.GetGenericIndexer<double>();
        
        for (int y = 0; y < rawMat.Height; y++)
            for (int x = 0; x < rawMat.Width; x++)
                samples.Add((x, y, indexer[y, x]));

        var initialGuess = Vector<double>.Build.Dense([maxVal, p.X, p.Y, 2.0, minVal]);

        try
        {
            Func<Vector<double>, double> objective = (par) =>
            {
                double error = 0;
                // par[0]: Amp, par[1]: xo, par[2]: yo, par[3]: sigma, par[4]: offset
                foreach (var s in samples)
                {
                    double model = par[4] + par[0] * Math.Exp(-0.5 * (Math.Pow(s.X - par[1], 2) + Math.Pow(s.Y - par[2], 2)) / (par[3] * par[3]));
                    error += Math.Pow(s.Val - model, 2);
                }
                return error;
            };

            var result = FindMinimum.OfFunction(objective, initialGuess);
            return new Point2D(result[1], result[2]);
        }
        catch { return new Point2D(p.X, p.Y); }
    }

    public Point2D FindPeak(Mat image, double sigma = 1.0)
    {
        using Mat b = new Mat();
        if (sigma > 0) Cv2.GaussianBlur(image, b, new Size(0, 0), sigma);
        else image.CopyTo(b);
        
        Cv2.MinMaxLoc(b, out _, out _, out _, out Point p);
        return new Point2D(p.X, p.Y);
    }

    public Point2D FindCentroid(Mat image, double sigma = 5.0)
    {
        Moments m = Cv2.Moments(image);
        return m.M00 != 0 ? new Point2D(m.M10 / m.M00, m.M01 / m.M00) : new Point2D(image.Width / 2.0, image.Height / 2.0);
    }

    // =======================================================================
    // 3. REGISTRAZIONE & SHIFT (FFT E TEMPLATE)
    // =======================================================================

    
    public (Point2D Shift, double Confidence) ComputeStarFieldShift(Mat reference, Mat target)
    {
        if (reference == null || target == null || reference.Empty() || target.Empty()) 
            return (new Point2D(0, 0), 0);

        using var ref32 = new Mat();
        using var tgt32 = new Mat();
        reference.ConvertTo(ref32, MatType.CV_32F);
        target.ConvertTo(tgt32, MatType.CV_32F);

        using var window = new Mat();
        Cv2.CreateHanningWindow(window, ref32.Size(), MatType.CV_32F);

        Point2d shift = Cv2.PhaseCorrelate(ref32, tgt32, window, out double response);

        return (new Point2D(shift.X, shift.Y), response);
    }

    public Point2D? FindTemplatePosition(Mat searchImage, Mat template, Point2D expectedCenter, int searchRadius)
    {
        if (searchImage.Empty() || template.Empty()) return null;

        // 1. Ritaglio area di ricerca (Search Window)
        int sx = (int)Math.Max(0, expectedCenter.X - searchRadius);
        int sy = (int)Math.Max(0, expectedCenter.Y - searchRadius);
        int sw = (int)Math.Min(searchRadius * 2, searchImage.Width - sx);
        int sh = (int)Math.Min(searchRadius * 2, searchImage.Height - sy);

        if (sw <= template.Width || sh <= template.Height) return null;

        using Mat searchRegion = new Mat(searchImage, new Rect(sx, sy, sw, sh));
        using Mat res = new Mat();
        
        // 2. Template Matching Normalizzato
        Cv2.MatchTemplate(searchRegion, template, res, TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(res, out _, out double maxVal, out _, out Point maxLoc);

        if (maxVal < 0.5) return null; // Confidenza insufficiente

        // 3. Ritorna coordinata globale (con raffinamento sub-pixel del picco)
        return new Point2D(maxLoc.X + sx + (template.Width / 2.0), maxLoc.Y + sy + (template.Height / 2.0));
    }
}