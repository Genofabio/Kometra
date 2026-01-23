using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using KomaLab.Models.Processing;
using OpenCvSharp;

namespace KomaLab.Services.Processing.Engines;

public class StackingEngine : IStackingEngine
{
    // Metodo "Massivo" per Mediana (richiede tutte le immagini)
    public async Task<Mat> ComputeStackAsync(List<Mat> sources, StackingMode mode)
    {
        if (sources == null || sources.Count == 0) 
            throw new ArgumentException("Nessuna immagine fornita per lo stacking.");

        int width = sources[0].Width;
        int height = sources[0].Height;
        
        Mat resultMat = new Mat(height, width, MatType.CV_64FC1, new Scalar(0));

        await Task.Run(() =>
        {
            if (mode == StackingMode.Median)
            {
                ExecuteMedianStack(sources, resultMat, width, height);
            }
            else
            {
                // Fallback per Somma/Media se qualcuno passa la lista intera
                // (Ma preferiamo la via incrementale dal Coordinator)
                using Mat countMat = new Mat(height, width, MatType.CV_64FC1, new Scalar(0));
                foreach (var src in sources)
                {
                    AccumulateFrame(resultMat, countMat, src);
                }
                if (mode == StackingMode.Average) FinalizeAverage(resultMat, countMat);
            }
        });

        return resultMat;
    }

    // --- NUOVI METODI PER STACKING INCREMENTALE (Low Memory) ---

    public void InitializeAccumulators(int width, int height, out Mat accumulator, out Mat countMap)
    {
        accumulator = new Mat(height, width, MatType.CV_64FC1, new Scalar(0));
        countMap = new Mat(height, width, MatType.CV_64FC1, new Scalar(0));
    }

    public void AccumulateFrame(Mat accumulator, Mat countMap, Mat currentFrame)
    {
        using Mat nonNanMask = new Mat();
        // 1. Maschera: (pixel == pixel) è falso solo per NaN.
        //    Usiamo EQ: True(255) se valido, False(0) se NaN.
        Cv2.Compare(currentFrame, currentFrame, nonNanMask, CmpType.EQ);
        
        // 2. Accumulo Valori (dove mask è true)
        Cv2.Add(accumulator, currentFrame, accumulator, mask: nonNanMask);
        
        // 3. Accumulo Conteggi (dove mask è true aggiungiamo 1.0)
        using Mat ones = new Mat(accumulator.Size(), MatType.CV_64FC1, new Scalar(1));
        Cv2.Add(countMap, ones, countMap, mask: nonNanMask);
    }

    public void FinalizeAverage(Mat accumulator, Mat countMap)
    {
        // Evitiamo divisione per zero: Max(count, 1.0)
        Cv2.Max(countMap, 1.0, countMap);
        Cv2.Divide(accumulator, countMap, accumulator, scale: 1, dtype: MatType.CV_64FC1);
    }

    // --- Algoritmo Mediana (Invariato, richiede molta RAM) ---

    private void ExecuteMedianStack(List<Mat> sources, Mat result, int w, int h)
    {
        int numFrames = sources.Count;
        int stripHeight = 64; 

        Parallel.For(0, (h + stripHeight - 1) / stripHeight, stripIndex =>
        {
            int yStart = stripIndex * stripHeight;
            int currentStripH = Math.Min(stripHeight, h - yStart);
            int pixelsInStrip = w * currentStripH;

            // Allocazione buffer ridotta all'interno dello strip
            double[][] frameBuffers = new double[numFrames][];
            for (int k = 0; k < numFrames; k++)
            {
                frameBuffers[k] = new double[pixelsInStrip];
                // Accesso diretto alla memoria (unsafe-like speed)
                Marshal.Copy(sources[k].Ptr(yStart), frameBuffers[k], 0, pixelsInStrip);
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
                    resultBuffer[i] = 0.0; // Meglio 0.0 che NaN per il risultato finale
                }
                else
                {
                    Array.Sort(pixelValues, 0, validCount);
                    resultBuffer[i] = (validCount % 2 == 0)
                        ? (pixelValues[validCount / 2 - 1] + pixelValues[validCount / 2]) * 0.5
                        : pixelValues[validCount / 2];
                }
            }

            Marshal.Copy(resultBuffer, 0, result.Ptr(yStart), pixelsInStrip);
        });
    }
}