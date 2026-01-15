using System;
using System.Runtime.InteropServices;
using KomaLab.Models.Fits; // Solo per l'enum FitsBitDepth
using OpenCvSharp;

namespace KomaLab.Services.Fits;

public class FitsOpenCvConverter : IFitsOpenCvConverter
{
    // =======================================================================
    // 1. RAW (Array) -> MAT (OPENCV)
    // =======================================================================

    /// <summary>
    /// Converte un array raw FITS in una matrice OpenCV Double.
    /// BSCALE e BZERO sono opzionali (default 1.0 e 0.0).
    /// </summary>
    public Mat RawToMat(Array rawPixels, double bScale = 1.0, double bZero = 0.0)
    {
        if (rawPixels == null) throw new ArgumentNullException(nameof(rawPixels));
        int height = rawPixels.GetLength(0); 
        // Passiamo i parametri opzionali
        return RawToMatRect(rawPixels, 0, height, bScale, bZero);
    }

    /// <summary>
    /// Converte una porzione dell'array.
    /// Parametri di scaling opzionali in fondo alla firma.
    /// </summary>
    public Mat RawToMatRect(Array rawPixels, int yStart, int rowsToRead, double bScale = 1.0, double bZero = 0.0)
    {
        if (rawPixels == null) throw new ArgumentNullException(nameof(rawPixels));

        int totalHeight = rawPixels.GetLength(0);
        int width = rawPixels.GetLength(1);

        if (yStart < 0 || rowsToRead <= 0 || yStart + rowsToRead > totalHeight)
            throw new ArgumentOutOfRangeException("Regione fuori dai limiti.");

        // Matrice destinazione sempre Double per calcoli scientifici precisi
        Mat stripMat = new Mat(rowsToRead, width, MatType.CV_64FC1);

        try
        {
            switch (rawPixels)
            {
                case byte[,] b: Copy2DToMat(b, stripMat, MatType.CV_8UC1, bScale, bZero, yStart); break;
                case short[,] s: Copy2DToMat(s, stripMat, MatType.CV_16SC1, bScale, bZero, yStart); break;
                case int[,] i: Copy2DToMat(i, stripMat, MatType.CV_32SC1, bScale, bZero, yStart); break;
                case float[,] f: Copy2DToMat(f, stripMat, MatType.CV_32FC1, bScale, bZero, yStart); break;
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

    private void Copy2DToMat<T>(T[,] source, Mat destDouble, MatType tempType, double bScale, double bZero, int yStart) 
        where T : struct
    {
        int h = destDouble.Rows;
        int w = destDouble.Cols;

        using Mat tempMat = new Mat(h, w, tempType);
        
        for (int y = 0; y < h; y++)
        {
            IntPtr destPtr = tempMat.Ptr(y);
            
            // Marshalling riga per riga per alte prestazioni
            T[] rowBuffer = new T[w];
            for (int x = 0; x < w; x++)
            {
                rowBuffer[x] = source[y + yStart, x];
            }
            
            MarshalCopyArrayToPtr(rowBuffer, 0, destPtr, w);
        }

        // OpenCV applica la trasformazione fisica qui:
        // Dest = Src * Scale + Zero
        // Se Scale=1 e Zero=0, fa solo una conversione di tipo ottimizzata.
        tempMat.ConvertTo(destDouble, MatType.CV_64FC1, bScale, bZero);
    }

    // =======================================================================
    // 2. MAT (OPENCV) -> RAW (Array Puro)
    // =======================================================================

    /// <summary>
    /// Converte una matrice OpenCV in un Array C# multidimensionale.
    /// Non restituisce Header: chi chiama questo metodo saprà creare l'header basandosi sul tipo dell'Array.
    /// </summary>
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
            _ => MatType.CV_64FC1
        };

        // Convertiamo se necessario (gestione memoria interna OpenCV)
        Mat sourceToRead = mat;
        bool tempCreated = false;
        
        if (mat.Type() != requiredType)
        {
            sourceToRead = new Mat();
            mat.ConvertTo(sourceToRead, requiredType);
            tempCreated = true;
        }

        try
        {
            switch (targetDepth)
            {
                case FitsBitDepth.UInt8:  return MatTo2DArray<byte>(sourceToRead, h, w);
                case FitsBitDepth.Int16:  return MatTo2DArray<short>(sourceToRead, h, w);
                case FitsBitDepth.Int32:  return MatTo2DArray<int>(sourceToRead, h, w);
                case FitsBitDepth.Float:  return MatTo2DArray<float>(sourceToRead, h, w);
                default:                  return MatTo2DArray<double>(sourceToRead, h, w);
            }
        }
        finally
        {
            if (tempCreated) sourceToRead.Dispose();
        }
    }

    // =======================================================================
    // 3. HELPERS (Marshalling)
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