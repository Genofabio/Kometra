using System;
using System.Runtime.InteropServices;
using KomaLab.Models.Fits;
using KomaLab.Models.Fits.Structure;
using OpenCvSharp;

namespace KomaLab.Services.Fits.Conversion;

public class FitsOpenCvConverter : IFitsOpenCvConverter
{
    // =======================================================================
    // 1. RAW (Array) -> MAT (OPENCV)
    // =======================================================================

    /// <summary>
    /// Converte un array raw FITS in una matrice OpenCV Floating Point.
    /// Se targetBitDepth è null, applica la promozione intelligente per preservare la precisione.
    /// </summary>
    public Mat RawToMat(Array rawPixels, double bScale = 1.0, double bZero = 0.0, FitsBitDepth? targetDepth = null)
    {
        if (rawPixels == null) throw new ArgumentNullException(nameof(rawPixels));
    
        int height = rawPixels.GetLength(0); 
    
        // Manteniamo la promozione intelligente: se null, decide DetermineOptimalDepth
        int depth;
        if (targetDepth.HasValue)
        {
            // Se l'utente specifica l'enum, mappiamo ai bit (32 o 64 per i float OpenCV)
            depth = (targetDepth.Value == FitsBitDepth.Float) ? 32 : 64;
        }
        else
        {
            depth = DetermineOptimalDepth(rawPixels);
        }
    
        return RawToMatRect(rawPixels, 0, height, bScale, bZero, depth);
    }

    // DetermineOptimalDepth rimane IDENTICO nella logica
    private int DetermineOptimalDepth(Array rawPixels)
    {
        return rawPixels switch
        {
            byte[,] or short[,] => 32,
            int[,] => 64,
            float[,] => 32,
            double[,] => 64,
            _ => 64
        };
    }

    /// <summary>
    /// Converte una porzione dell'array con profondità di bit specificata.
    /// </summary>
    private Mat RawToMatRect(Array rawPixels, int yStart, int rowsToRead, double bScale = 1.0, double bZero = 0.0, int targetBitDepth = 64)
    {
        if (rawPixels == null) throw new ArgumentNullException(nameof(rawPixels));

        int totalHeight = rawPixels.GetLength(0);
        int width = rawPixels.GetLength(1);

        if (yStart < 0 || rowsToRead <= 0 || yStart + rowsToRead > totalHeight)
            throw new ArgumentOutOfRangeException(nameof(yStart), "Regione fuori dai limiti dell'immagine.");

        // Configurazione destinazione
        MatType destinationType = (targetBitDepth == 32) ? MatType.CV_32FC1 : MatType.CV_64FC1;
        Mat stripMat = new Mat(rowsToRead, width, destinationType);

        try
        {
            switch (rawPixels)
            {
                case byte[,] b:   Copy2DToMat(b, stripMat, MatType.CV_8UC1, bScale, bZero, yStart); break;
                case short[,] s:  Copy2DToMat(s, stripMat, MatType.CV_16SC1, bScale, bZero, yStart); break;
                case int[,] i:    Copy2DToMat(i, stripMat, MatType.CV_32SC1, bScale, bZero, yStart); break;
                case float[,] f:  Copy2DToMat(f, stripMat, MatType.CV_32FC1, bScale, bZero, yStart); break;
                case double[,] d: Copy2DToMat(d, stripMat, MatType.CV_64FC1, bScale, bZero, yStart); break;
                default: throw new NotSupportedException($"Tipo array {rawPixels.GetType()} non supportato.");
            }
        }
        catch
        {
            stripMat.Dispose();
            throw;
        }

        return stripMat;
    }

    private void Copy2DToMat<T>(T[,] source, Mat destMat, MatType sourceTempType, double bScale, double bZero, int yStart) 
        where T : struct
    {
        int h = destMat.Rows;
        int w = destMat.Cols;

        // 1. Caricamento dati grezzi in una matrice temporanea del tipo originale
        using Mat tempMat = new Mat(h, w, sourceTempType);
        T[] rowBuffer = new T[w]; 

        for (int y = 0; y < h; y++)
        {
            IntPtr destPtr = tempMat.Ptr(y);
            for (int x = 0; x < w; x++)
            {
                rowBuffer[x] = source[y + yStart, x];
            }
            MarshalCopyArrayToPtr(rowBuffer, 0, destPtr, w);
        }

        // 2. Conversione e Scaling (BSCALE/BZERO) verso il tipo floating point finale
        tempMat.ConvertTo(destMat, destMat.Type(), bScale, bZero);
    }

    // =======================================================================
    // 2. MAT (OPENCV) -> RAW (Array Puro)
    // =======================================================================

    public Array MatToRaw(Mat mat, FitsBitDepth targetDepth = FitsBitDepth.Double)
    {
        if (mat.Empty()) throw new ArgumentException("Matrice nulla o vuota", nameof(mat));

        int w = mat.Width;
        int h = mat.Height;
        
        MatType requiredType = targetDepth switch
        {
            FitsBitDepth.UInt8  => MatType.CV_8UC1,
            FitsBitDepth.Int16  => MatType.CV_16SC1,
            FitsBitDepth.Int32  => MatType.CV_32SC1,
            FitsBitDepth.Float  => MatType.CV_32FC1,
            _                   => MatType.CV_64FC1
        };

        Mat sourceToRead = mat;
        bool tempCreated = false;
        
        // Se la matrice non è già nel formato richiesto, eseguiamo la conversione
        if (mat.Type() != requiredType)
        {
            sourceToRead = new Mat();
            mat.ConvertTo(sourceToRead, requiredType);
            tempCreated = true;
        }

        try
        {
            return targetDepth switch
            {
                FitsBitDepth.UInt8 => MatTo2DArray<byte>(sourceToRead, h, w),
                FitsBitDepth.Int16 => MatTo2DArray<short>(sourceToRead, h, w),
                FitsBitDepth.Int32 => MatTo2DArray<int>(sourceToRead, h, w),
                FitsBitDepth.Float => MatTo2DArray<float>(sourceToRead, h, w),
                _                  => MatTo2DArray<double>(sourceToRead, h, w),
            };
        }
        finally
        {
            if (tempCreated) sourceToRead.Dispose();
        }
    }

    // =======================================================================
    // 3. HELPERS (Marshalling & Memory)
    // =======================================================================

    private T[,] MatTo2DArray<T>(Mat mat, int rows, int cols) where T : struct
    {
        T[,] result = new T[rows, cols];
        T[] rowBuffer = new T[cols];

        for (int y = 0; y < rows; y++)
        {
            IntPtr srcPtr = mat.Ptr(y);
            MarshalCopyPtrToArray(srcPtr, rowBuffer, cols);
            for (int x = 0; x < cols; x++) result[y, x] = rowBuffer[x];
        }
        return result;
    }

    private void MarshalCopyPtrToArray<T>(IntPtr sourcePtr, T[] destArray, int length) where T : struct
    {
        if (destArray is byte[] b) Marshal.Copy(sourcePtr, b, 0, length);
        else if (destArray is short[] s) Marshal.Copy(sourcePtr, s, 0, length);
        else if (destArray is int[] i) Marshal.Copy(sourcePtr, i, 0, length);
        else if (destArray is float[] f) Marshal.Copy(sourcePtr, f, 0, length);
        else if (destArray is double[] d) Marshal.Copy(sourcePtr, d, 0, length);
        else throw new NotSupportedException($"Tipo {typeof(T)} non supportato.");
    }

    private void MarshalCopyArrayToPtr<T>(T[] sourceArray, int startIndex, IntPtr destinationPtr, int length) 
        where T : struct
    {
        if (sourceArray is byte[] b) Marshal.Copy(b, startIndex, destinationPtr, length);
        else if (sourceArray is short[] s) Marshal.Copy(s, startIndex, destinationPtr, length);
        else if (sourceArray is int[] i) Marshal.Copy(i, startIndex, destinationPtr, length);
        else if (sourceArray is float[] f) Marshal.Copy(f, startIndex, destinationPtr, length);
        else if (sourceArray is double[] d) Marshal.Copy(d, startIndex, destinationPtr, length);
        else throw new NotSupportedException($"Tipo {typeof(T)} non supportato.");
    }
}