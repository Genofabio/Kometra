using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices; // Serve per Marshal
using System.Threading.Tasks;
using KomaLab.Models.Processing;
using OpenCvSharp;

namespace KomaLab.Services.Processing.Engines;

public class StackingEngine : IStackingEngine
{
    // =================================================================================
    // 1. MODALITÀ "MASSIVA"
    // =================================================================================
    
    public async Task<Mat> ComputeStackAsync(List<Mat> sources, StackingMode mode)
    {
        if (sources == null || sources.Count == 0) 
            throw new ArgumentException("Nessuna immagine fornita.");

        int width = sources[0].Width;
        int height = sources[0].Height;
        
        Mat resultMat = new Mat(height, width, MatType.CV_64FC1, new Scalar(0));

        await Task.Run(() =>
        {
            if (mode == StackingMode.Median)
            {
                // Trattiamo l'intera immagine come un'unica grande striscia
                // Nota: Convertiamo la List in Array per accesso veloce per indice
                ProcessMedianStripSafe(sources.ToArray(), resultMat, 0, width, height);
            }
            else
            {
                // Fallback Somma/Media
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

    // =================================================================================
    // 2. MODALITÀ "LOW MEMORY" (Chunked Median)
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

        Mat resultMat = new Mat(height, width, MatType.CV_64FC1, new Scalar(0));

        // Calcolo Budget RAM (target 200MB)
        long bytesPerRow = (long)width * numFrames * 8; 
        if (bytesPerRow == 0) bytesPerRow = 1;
        
        int maxMemBytes = 200 * 1024 * 1024; 
        int stripHeight = Math.Max(1, (int)(maxMemBytes / bytesPerRow));
        stripHeight = (stripHeight / 8 + 1) * 8; // Allineamento

        for (int y = 0; y < height; y += stripHeight)
        {
            int currentH = Math.Min(stripHeight, height - y);
            Rect currentRect = new Rect(0, y, width, currentH);

            var loadingTasks = sourceList.Select(src => regionLoader(src, currentRect));
            Mat[] stackStrip = await Task.WhenAll(loadingTasks);

            try
            {
                // Chiamata al metodo SAFE
                ProcessMedianStripSafe(stackStrip, resultMat, y, width, currentH);
            }
            finally
            {
                foreach (var m in stackStrip) m.Dispose();
            }
        }

        return resultMat;
    }

    // =================================================================================
    // 3. KERNEL MEDIANA "SAFE" (No Unsafe, No Pointers)
    // =================================================================================

    private void ProcessMedianStripSafe(Mat[] stack, Mat resultFull, int yStart, int w, int h)
    {
        int numFrames = stack.Length;
        int pixelsInStrip = w * h;

        // 1. PREPARAZIONE DATI (Copia da Unmanaged a Managed)
        // Creiamo array C# per lavorare velocemente in memoria gestita.
        // Array di array: buffer[frameIndex][pixelIndex]
        double[][] managedInputs = new double[numFrames][];

        // Parallelizziamo la copia per velocizzare il trasferimento dati
        Parallel.For(0, numFrames, k =>
        {
            managedInputs[k] = new double[pixelsInStrip];
            
            // Copiamo riga per riga per gestire correttamente il padding (Step) della Mat
            // Se la Mat è "Continuous", potremmo copiare tutto in un colpo, ma riga per riga è più sicuro.
            long step = stack[k].Step(); // Byte per riga
            IntPtr dataPtr = stack[k].Data;

            for (int row = 0; row < h; row++)
            {
                // Calcoliamo l'indirizzo della riga 'row'
                IntPtr rowPtr = IntPtr.Add(dataPtr, (int)(row * step));
                
                // Copiamo la riga nell'array gestito all'offset corretto
                Marshal.Copy(rowPtr, managedInputs[k], row * w, w);
            }
        });

        // Array per il risultato della striscia
        double[] managedResult = new double[pixelsInStrip];

        // 2. CALCOLO (Puro C# gestito)
        // Usiamo Parallel.For con buffer locale per il sorting
        Parallel.For(0, pixelsInStrip, 
            () => new double[numFrames], // Init buffer locale
            (i, state, buffer) => 
            {
                int validCount = 0;

                // Loop sui frame (accesso array managed è velocissimo)
                for (int k = 0; k < numFrames; k++)
                {
                    double val = managedInputs[k][i];
                    if (!double.IsNaN(val))
                    {
                        buffer[validCount++] = val;
                    }
                }

                if (validCount == 0)
                {
                    managedResult[i] = 0.0;
                }
                else
                {
                    Array.Sort(buffer, 0, validCount);
                    if (validCount % 2 == 1)
                        managedResult[i] = buffer[validCount / 2];
                    else
                        managedResult[i] = (buffer[validCount / 2 - 1] + buffer[validCount / 2]) * 0.5;
                }

                return buffer;
            },
            (buffer) => { } // Finally
        );

        // 3. SCRITTURA RISULTATO (Copia da Managed a Unmanaged)
        // Copiamo managedResult dentro resultFull alla posizione corretta (yStart)
        long resStep = resultFull.Step();
        IntPtr resBasePtr = resultFull.Data;

        // Non possiamo parallelizzare facilmente la scrittura su resultFull se non siamo attenti,
        // ma un loop sequenziale di copia memoria è molto veloce.
        for (int row = 0; row < h; row++)
        {
            // Dove scrivere nell'immagine finale: yStart + row corrente
            int absoluteRow = yStart + row;
            
            // Puntatore alla riga nell'immagine destinazione
            IntPtr dstRowPtr = IntPtr.Add(resBasePtr, (int)(absoluteRow * resStep));
            
            // Copiamo dall'array risultato (offset row * w) alla memoria nativa
            Marshal.Copy(managedResult, row * w, dstRowPtr, w);
        }
    }

    // =================================================================================
    // 4. METODI INCREMENTALI (Invariati)
    // =================================================================================

    public void InitializeAccumulators(int width, int height, out Mat accumulator, out Mat countMap)
    {
        accumulator = new Mat(height, width, MatType.CV_64FC1, new Scalar(0));
        countMap = new Mat(height, width, MatType.CV_64FC1, new Scalar(0));
    }

    public void AccumulateFrame(Mat accumulator, Mat countMap, Mat currentFrame)
    {
        using Mat nonNanMask = new Mat();
        Cv2.Compare(currentFrame, currentFrame, nonNanMask, CmpType.EQ);
        Cv2.Add(accumulator, currentFrame, accumulator, mask: nonNanMask);
        
        using Mat ones = new Mat(accumulator.Size(), MatType.CV_64FC1, new Scalar(1));
        Cv2.Add(countMap, ones, countMap, mask: nonNanMask);
    }

    public void FinalizeAverage(Mat accumulator, Mat countMap)
    {
        Cv2.Max(countMap, 1.0, countMap);
        Cv2.Divide(accumulator, countMap, accumulator, scale: 1, dtype: MatType.CV_64FC1);
    }
}