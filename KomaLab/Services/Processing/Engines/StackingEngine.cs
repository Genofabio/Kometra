using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using KomaLab.Models.Processing;
using OpenCvSharp;

namespace KomaLab.Services.Processing.Engines;

public class StackingEngine : IStackingEngine
{
    public async Task<Mat> ComputeStackAsync(List<Mat> sources, StackingMode mode)
    {
        if (sources == null || sources.Count == 0) 
            throw new ArgumentException("Nessuna immagine fornita per lo stacking.");

        int width = sources[0].Width;
        int height = sources[0].Height;
        
        // Creiamo la matrice di destinazione (Sempre Double per la massima dinamica scientifica)
        Mat resultMat = new Mat(height, width, MatType.CV_64FC1, new Scalar(0));

        await Task.Run(() =>
        {
            switch (mode)
            {
                case StackingMode.Sum:
                case StackingMode.Average:
                    ExecuteLinearStack(sources, resultMat, mode, width, height);
                    break;

                case StackingMode.Median:
                    ExecuteMedianStack(sources, resultMat, width, height);
                    break;
            }
        });

        return resultMat;
    }

    // --- Algoritmi Lineari (Veloce O(N)) ---

    private void ExecuteLinearStack(List<Mat> sources, Mat result, StackingMode mode, int w, int h)
    {
        using Mat validCountMat = new Mat(h, w, MatType.CV_64FC1, new Scalar(0));
        using Mat onesMat = new Mat(h, w, MatType.CV_64FC1, new Scalar(1));

        foreach (var currentMat in sources)
        {
            using Mat nonNanMask = new Mat();
            // Troviamo i pixel validi (Ignoriamo i NaN derivanti dall'allineamento)
            Cv2.Compare(currentMat, currentMat, nonNanMask, CmpType.EQ);
            
            Cv2.Add(result, currentMat, result, mask: nonNanMask);
            Cv2.Add(validCountMat, onesMat, validCountMat, mask: nonNanMask);
        }

        if (mode == StackingMode.Average)
        {
            // Divisione pixel-per-pixel per il numero di frame validi trovati
            using Mat safeDivisor = validCountMat.Clone();
            Cv2.Max(safeDivisor, 1.0, safeDivisor);
            Cv2.Divide(result, safeDivisor, result, scale: 1, dtype: MatType.CV_64FC1);
        }
    }

    // --- Algoritmo Mediana (Intensivo O(N log N)) ---

    private void ExecuteMedianStack(List<Mat> sources, Mat result, int w, int h)
    {
        int numFrames = sources.Count;
        int stripHeight = 64; // Ottimizzato per la cache L2/L3

        Parallel.For(0, (h + stripHeight - 1) / stripHeight, stripIndex =>
        {
            int yStart = stripIndex * stripHeight;
            int currentStripH = Math.Min(stripHeight, h - yStart);
            int pixelsInStrip = w * currentStripH;

            // Buffer per ogni frame nella striscia corrente
            double[][] frameBuffers = new double[numFrames][];
            for (int k = 0; k < numFrames; k++)
            {
                frameBuffers[k] = new double[pixelsInStrip];
                for (int r = 0; r < currentStripH; r++)
                {
                    Marshal.Copy(sources[k].Ptr(yStart + r), frameBuffers[k], r * w, w);
                }
            }

            double[] resultBuffer = new double[pixelsInStrip];
            double[] pixelValues = new double[numFrames];

            for (int i = 0; i < pixelsInStrip; i++)
            {
                int validCount = 0;
                for (int k = 0; k < numFrames; k++)
                {
                    double v = frameBuffers[k][i];
                    if (!double.IsNaN(v)) pixelValues[validCount++] = v;
                }

                if (validCount == 0)
                {
                    resultBuffer[i] = double.NaN;
                }
                else
                {
                    Array.Sort(pixelValues, 0, validCount);
                    // Calcolo mediana (pari o dispari)
                    resultBuffer[i] = (validCount % 2 == 0)
                        ? (pixelValues[validCount / 2 - 1] + pixelValues[validCount / 2]) * 0.5
                        : pixelValues[validCount / 2];
                }
            }

            // Scrittura della striscia elaborata nella matrice finale
            for (int r = 0; r < currentStripH; r++)
            {
                Marshal.Copy(resultBuffer, r * w, result.Ptr(yStart + r), w);
            }
        });
    }
}