using System;
using System.Collections.Generic;
using KomaLab.Models.Visualization;
using MathNet.Numerics.Statistics;
using OpenCvSharp;

namespace KomaLab.Services.Processing.Engines;

public class RadiometryEngine : IRadiometryEngine
{
    public RadiometryEngine() { }

    // =======================================================================
    // 1. STATISTICHE DI BASE
    // =======================================================================

    public (double Mean, double StdDev) ComputeStatistics(Mat image)
    {
        if (image == null || image.Empty()) return (0, 1);
        
        using Mat meanMat = new Mat();
        using Mat stdDevMat = new Mat();
        using Mat mask = new Mat();
        
        Cv2.Compare(image, image, mask, CmpType.EQ); 
        Cv2.MeanStdDev(image, meanMat, stdDevMat, mask);
        
        double mean = meanMat.At<double>(0, 0);
        double std = stdDevMat.At<double>(0, 0);
        
        if (double.IsNaN(mean)) mean = 0;
        if (double.IsNaN(std) || std < 1e-9) std = 1.0;
        
        return (mean, std);
    }

    public double[] GetPixelSamples(Mat image, int maxSamples = 10000)
    {
        return ExtractPixelSamples(image, maxSamples);
    }

    // =======================================================================
    // 2. LOGICA AUTOSTRETCH (QUANTILI VS SIGMA)
    // =======================================================================

    public AbsoluteContrastProfile CalculateAutoStretchProfile(Mat image)
    {
        if (image == null || image.Empty()) return new AbsoluteContrastProfile(0, 65535);

        var samples = ExtractPixelSamples(image, 10000);
        return CalculateAutoStretchFromSamples(samples);
    }

    public AbsoluteContrastProfile CalculateAutoStretchFromSamples(double[] samples)
    {
        if (samples == null || samples.Length == 0) 
            return new AbsoluteContrastProfile(0, 65535);

        // Strategia Astronomica (basata su istogramma):
        // Black al 30% per non tagliare il fondo cielo, White al 99.5% per le stelle.
        double b = Statistics.Quantile(samples, 0.30);  
        double w = Statistics.Quantile(samples, 0.995); 

        if (Math.Abs(w - b) < 1e-6) w = b + 100.0; 

        return new AbsoluteContrastProfile(b, w);
    }

    public AbsoluteContrastProfile CalculateAutoStretchFromStats(double mean, double stdDev)
    {
        // Metodo Sigma-Clip (Veloce, ottimo per transizioni e calcoli O(1)):
        // Mappa tipicamente il fondo cielo a -2.8 sigma e le luci a +15 sigma.
        double b = mean - (2.8 * stdDev);
        double w = mean + (15.0 * stdDev);
        
        return new AbsoluteContrastProfile(b, w);
    }

    // =======================================================================
    // 3. LOGICA DI ADATTAMENTO (TRANSITIONS)
    // =======================================================================

    public AbsoluteContrastProfile CalculateAdaptedProfile(
        Mat sourceMat, 
        Mat targetMat, 
        double currentBlack, 
        double currentWhite,
        (double Mean, double StdDev)? sourceStats = null,
        (double Mean, double StdDev)? targetStats = null)
    {
        var sStats = sourceStats ?? ComputeStatistics(sourceMat);
        var tStats = targetStats ?? ComputeStatistics(targetMat);

        // Calcoliamo quanto l'utente è distante dalla media in termini di Sigma
        var sigmaProfile = ComputeSigmaProfile(sourceMat, currentBlack, currentWhite, sStats);
        
        // Applichiamo quella distanza alla nuova immagine
        return ComputeAbsoluteFromSigma(targetMat, sigmaProfile, tStats);
    }

    public SigmaContrastProfile ComputeSigmaProfile(Mat? image, double currentBlack, double currentWhite, (double Mean, double StdDev)? stats = null)
    {
        var (mean, sigma) = stats ?? (image != null ? ComputeStatistics(image) : (0.0, 1.0));
        
        if (sigma < 1e-9) sigma = 1.0;

        double kBlack = (currentBlack - mean) / sigma;
        double kWhite = (currentWhite - mean) / sigma;

        return new SigmaContrastProfile(kBlack, kWhite);
    }

    public AbsoluteContrastProfile ComputeAbsoluteFromSigma(Mat? image, SigmaContrastProfile profile, (double Mean, double StdDev)? stats = null)
    {
        var (mean, sigma) = stats ?? (image != null ? ComputeStatistics(image) : (0.0, 1.0));

        double newBlack = mean + (profile.KBlack * sigma);
        double newWhite = mean + (profile.KWhite * sigma);

        return new AbsoluteContrastProfile(newBlack, newWhite);
    }

    // =======================================================================
    // HELPERS PRIVATI
    // =======================================================================

    private double[] ExtractPixelSamples(Mat image, int maxSamples)
    {
        int width = image.Width;
        int height = image.Height;
        long totalPixels = (long)width * height;
        
        if (totalPixels == 0) return Array.Empty<double>();

        int step = (int)Math.Max(1, totalPixels / maxSamples);
        var samples = new List<double>(maxSamples);
        
        // Accesso generico sicuro ai dati (OpenCV Mat 32F o 64F)
        var indexer = image.GetGenericIndexer<double>();

        for (int i = 0; i < totalPixels; i += step)
        {
            int y = i / width;
            int x = i % width;
            if (y >= height) break;

            double val = indexer[y, x];
            if (!double.IsNaN(val) && !double.IsInfinity(val))
                samples.Add(val);
        }
        return samples.ToArray();
    }
}