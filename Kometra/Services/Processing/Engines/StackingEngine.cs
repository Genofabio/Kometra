using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Kometra.Models.Processing;
using Kometra.Models.Processing.Stacking;
using OpenCvSharp;

namespace Kometra.Services.Processing.Engines;

public class StackingEngine : IStackingEngine
{
    // =================================================================================
    // 1. MODALITÀ "MASSIVA" (Smart - Auto Detect)
    // =================================================================================
    
    public async Task<Mat> ComputeStackAsync(List<Mat> sources, StackingMode mode)
    {
        if (sources == null || sources.Count == 0) 
            throw new ArgumentException("Nessuna immagine fornita.");

        // Smart Detection: Rileviamo il tipo dalla prima immagine (Float o Double)
        MatType type = sources[0].Type();
        bool isDouble = type == MatType.CV_64FC1;

        int width = sources[0].Width;
        int height = sources[0].Height;
        
        // Il risultato avrà lo STESSO tipo dell'input
        Mat resultMat = new Mat(height, width, type, new Scalar(0));

        await Task.Run(() =>
        {
            if (mode == StackingMode.Median)
            {
                if (isDouble)
                    ProcessMedianStripDouble(sources.ToArray(), resultMat, 0, width, height);
                else
                    ProcessMedianStripFloat(sources.ToArray(), resultMat, 0, width, height);
            }
            else
            {
                // Fallback Somma/Media
                using Mat countMat = new Mat(height, width, type, new Scalar(0));
                foreach (var src in sources)
                {
                    AccumulateFrame(resultMat, countMat, src);
                }
                if (mode == StackingMode.Average) FinalizeAverage(resultMat, countMat);
            }
        });

        return resultMat;
    }

    // =================================================================================
    // 2. MODALITÀ "LOW MEMORY" (Smart Chunked)
    // =================================================================================

    public async Task<Mat> ComputeMedianChunkedAsync<TSource>(
        IEnumerable<TSource> sources, 
        int width, 
        int height,
        Func<TSource, Rect, Task<Mat>> regionLoader)
    {
        var sourceList = sources.ToList();
        int numFrames = sourceList.Count;
        if (numFrames == 0) throw new ArgumentException("Nessuna fonte fornita.");

        Mat resultMat = null; // Inizializzazione pigra (Lazy)
        bool isDouble = false;

        // Calcolo Budget RAM (Stimiamo per eccesso 8 byte, poi adatteremo)
        long bytesPerRow = (long)width * numFrames * 8; 
        if (bytesPerRow == 0) bytesPerRow = 1;
        
        // 200MB di buffer sono un buon compromesso
        int maxMemBytes = 200 * 1024 * 1024; 
        int stripHeight = Math.Max(1, (int)(maxMemBytes / bytesPerRow));
        stripHeight = (stripHeight / 8 + 1) * 8; // Allineamento a 8 righe

        for (int y = 0; y < height; y += stripHeight)
        {
            int currentH = Math.Min(stripHeight, height - y);
            Rect currentRect = new Rect(0, y, width, currentH);

            // Carichiamo lo strip (in parallelo grazie al Coordinator)
            var loadingTasks = sourceList.Select(src => regionLoader(src, currentRect));
            Mat[] stackStrip = await Task.WhenAll(loadingTasks);

            try
            {
                // AL PRIMO GIRO: Inizializziamo resultMat in base a cosa abbiamo caricato
                if (resultMat == null)
                {
                    MatType inputType = stackStrip[0].Type();
                    isDouble = (inputType == MatType.CV_64FC1);
                    resultMat = new Mat(height, width, inputType, new Scalar(0));
                }

                // Chiamiamo il kernel corretto
                if (isDouble)
                    ProcessMedianStripDouble(stackStrip, resultMat, y, width, currentH);
                else
                    ProcessMedianStripFloat(stackStrip, resultMat, y, width, currentH);
            }
            finally
            {
                foreach (var m in stackStrip) m.Dispose();
            }
        }

        // Fallback safe: se resultMat è null (0 frame), ritorniamo float nero
        return resultMat ?? new Mat(height, width, MatType.CV_32FC1, new Scalar(0));
    }

    // =================================================================================
    // 3. KERNEL FLOAT (32-bit)
    // =================================================================================

    private void ProcessMedianStripFloat(Mat[] stack, Mat resultFull, int yStart, int w, int h)
    {
        int numFrames = stack.Length;
        int pixelsInStrip = w * h;
        float[][] managedInputs = new float[numFrames][];

        // 1. Marshal Input (Copia veloce da OpenCV a C#)
        Parallel.For(0, numFrames, k =>
        {
            managedInputs[k] = new float[pixelsInStrip];
            long step = stack[k].Step(); 
            IntPtr dataPtr = stack[k].Data;
            for (int row = 0; row < h; row++)
                Marshal.Copy(IntPtr.Add(dataPtr, (int)(row * step)), managedInputs[k], row * w, w);
        });

        float[] managedResult = new float[pixelsInStrip];

        // 2. Calcolo Mediana Float
        Parallel.For(0, pixelsInStrip, () => new float[numFrames], (i, state, buffer) => 
        {
            int validCount = 0;
            for (int k = 0; k < numFrames; k++)
            {
                float val = managedInputs[k][i];
                if (!float.IsNaN(val)) buffer[validCount++] = val;
            }

            if (validCount == 0) managedResult[i] = 0.0f;
            else
            {
                Array.Sort(buffer, 0, validCount);
                if (validCount % 2 == 1) managedResult[i] = buffer[validCount / 2];
                else managedResult[i] = (buffer[validCount / 2 - 1] + buffer[validCount / 2]) * 0.5f;
            }
            return buffer;
        }, (buffer) => { });

        // 3. Marshal Output (Copia veloce da C# a OpenCV)
        long resStep = resultFull.Step();
        IntPtr resBasePtr = resultFull.Data;
        for (int row = 0; row < h; row++)
            Marshal.Copy(managedResult, row * w, IntPtr.Add(resBasePtr, (int)((yStart + row) * resStep)), w);
    }

    // =================================================================================
    // 4. KERNEL DOUBLE (64-bit)
    // =================================================================================

    private void ProcessMedianStripDouble(Mat[] stack, Mat resultFull, int yStart, int w, int h)
    {
        int numFrames = stack.Length;
        int pixelsInStrip = w * h;
        double[][] managedInputs = new double[numFrames][];

        // 1. Marshal Input
        Parallel.For(0, numFrames, k =>
        {
            managedInputs[k] = new double[pixelsInStrip];
            long step = stack[k].Step(); 
            IntPtr dataPtr = stack[k].Data;
            for (int row = 0; row < h; row++)
                Marshal.Copy(IntPtr.Add(dataPtr, (int)(row * step)), managedInputs[k], row * w, w);
        });

        double[] managedResult = new double[pixelsInStrip];

        // 2. Calcolo Mediana Double
        Parallel.For(0, pixelsInStrip, () => new double[numFrames], (i, state, buffer) => 
        {
            int validCount = 0;
            for (int k = 0; k < numFrames; k++)
            {
                double val = managedInputs[k][i];
                if (!double.IsNaN(val)) buffer[validCount++] = val;
            }

            if (validCount == 0) managedResult[i] = 0.0;
            else
            {
                Array.Sort(buffer, 0, validCount);
                if (validCount % 2 == 1) managedResult[i] = buffer[validCount / 2];
                else managedResult[i] = (buffer[validCount / 2 - 1] + buffer[validCount / 2]) * 0.5;
            }
            return buffer;
        }, (buffer) => { });

        // 3. Marshal Output
        long resStep = resultFull.Step();
        IntPtr resBasePtr = resultFull.Data;
        for (int row = 0; row < h; row++)
            Marshal.Copy(managedResult, row * w, IntPtr.Add(resBasePtr, (int)((yStart + row) * resStep)), w);
    }

    // =================================================================================
    // 5. METODI INCREMENTALI (Adattivi e Parametrici)
    // =================================================================================

    /// <summary>
    /// Inizializza gli accumulatori specificando la precisione desiderata.
    /// </summary>
    /// <param name="useDoublePrecision">
    /// Se true, usa CV_64FC1 (Alta precisione/Memoria). 
    /// Se false, usa CV_32FC1 (Standard/Veloce).
    /// </param>
    public void InitializeAccumulators(
        int width, 
        int height, 
        bool useDoublePrecision, 
        out Mat accumulator, 
        out Mat countMap)
    {
        MatType type = useDoublePrecision ? MatType.CV_64FC1 : MatType.CV_32FC1;
        
        accumulator = new Mat(height, width, type, new Scalar(0));
        countMap = new Mat(height, width, type, new Scalar(0));
    }

    public void AccumulateFrame(Mat accumulator, Mat countMap, Mat currentFrame)
    {
        // FIX BUG SOMMA: Gestione Mismatch Tipi (es. Input Float -> Accumulatore Double)
        // Se i tipi sono diversi, convertiamo il frame corrente per matchare l'accumulatore.
        if (currentFrame.Type() != accumulator.Type())
        {
            using Mat temp = new Mat();
            // Convertiamo (es. da Float a Double)
            currentFrame.ConvertTo(temp, accumulator.Type());
            
            // Richiamata ricorsiva con il tipo corretto
            AccumulateFrame(accumulator, countMap, temp);
            return;
        }

        // Se siamo qui, i tipi sono identici (Float+Float o Double+Double). Procediamo.
        using Mat mask = new Mat();
        Cv2.Compare(currentFrame, currentFrame, mask, CmpType.EQ); // NaN Check
        
        Cv2.Add(accumulator, currentFrame, accumulator, mask: mask, dtype: accumulator.Type().Value);
        
        using Mat ones = new Mat(accumulator.Size(), accumulator.Type(), new Scalar(1));
        Cv2.Add(countMap, ones, countMap, mask: mask);
    }

    public void FinalizeAverage(Mat accumulator, Mat countMap)
    {
        // Evitiamo divisione per zero
        Cv2.Max(countMap, 1.0, countMap);
        
        // Divisione finale: Accumulatore / Conteggi
        // dtype assicura che il risultato rimanga nel formato dell'accumulatore (Float o Double)
        Cv2.Divide(accumulator, countMap, accumulator, scale: 1, dtype: accumulator.Type().Value);
    }
}