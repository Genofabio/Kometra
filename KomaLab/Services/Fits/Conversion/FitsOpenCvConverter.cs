using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using KomaLab.Models.Fits;
using KomaLab.Models.Fits.Structure;
using OpenCvSharp;

namespace KomaLab.Services.Fits.Conversion;

public class FitsOpenCvConverter : IFitsOpenCvConverter
{
    // =======================================================================
    // 1. LETTURA (RAW -> OPENCV)
    // =======================================================================

    public Mat RawToMat(Array rawPixels, double bScale = 1.0, double bZero = 0.0, FitsBitDepth? targetDepth = null)
    {
        if (rawPixels == null) throw new ArgumentNullException(nameof(rawPixels));

        int rows = rawPixels.GetLength(0);
        int cols = rawPixels.GetLength(1);

        // --- DIAGNOSTICA VELOCE ---
        Debug.WriteLine($"[FitsConverter] Fast Path: {cols}x{rows}, Type: {rawPixels.GetType().Name}");

        if (rows == 0 || cols == 0) return new Mat();

        int depthBit = targetDepth.HasValue 
            ? (targetDepth.Value == FitsBitDepth.Float ? 32 : 64) 
            : DetermineOptimalDepth(rawPixels);
            
        MatType destType = (depthBit == 32) ? MatType.CV_32FC1 : MatType.CV_64FC1;

        // FIX UNIFORMITÀ: Se arriva unsigned nativo, azzeriamo bZero.
        if (rawPixels is ushort[,] || rawPixels is uint[,]) bZero = 0.0;

        // 1. PINNING: Blocchiamo l'array in memoria (Safe Interop)
        GCHandle handle = GCHandle.Alloc(rawPixels, GCHandleType.Pinned);

        try
        {
            IntPtr rawDataPtr = handle.AddrOfPinnedObject();
            Type type = rawPixels.GetType().GetElementType();

            // 2. WRAPPER: Creiamo una vista OpenCV sui dati C#
            using Mat rawWrapper = CreateWrapperWithFix(rows, cols, type, rawDataPtr);

            // 3. CONVERSIONE
            Mat result = new Mat(rows, cols, destType);
            rawWrapper.ConvertTo(result, destType, bScale, bZero);

            return result;
        }
        finally
        {
            if (handle.IsAllocated) handle.Free();
        }
    }

    private Mat CreateWrapperWithFix(int rows, int cols, Type elementType, IntPtr ptr)
    {
        // FIX CRITICO "SOLE NERO": Short -> UShort (CV_16U)
        if (elementType == typeof(short)) return Mat.FromPixelData(rows, cols, MatType.CV_16UC1, ptr);
        
        if (elementType == typeof(ushort)) return Mat.FromPixelData(rows, cols, MatType.CV_16UC1, ptr);
        if (elementType == typeof(byte)) return Mat.FromPixelData(rows, cols, MatType.CV_8UC1, ptr);
        if (elementType == typeof(sbyte)) return Mat.FromPixelData(rows, cols, MatType.CV_8SC1, ptr);
        if (elementType == typeof(int)) return Mat.FromPixelData(rows, cols, MatType.CV_32SC1, ptr);
        if (elementType == typeof(uint)) return Mat.FromPixelData(rows, cols, MatType.CV_32SC1, ptr); 
        if (elementType == typeof(float)) return Mat.FromPixelData(rows, cols, MatType.CV_32FC1, ptr);
        if (elementType == typeof(double)) return Mat.FromPixelData(rows, cols, MatType.CV_64FC1, ptr);

        throw new NotSupportedException($"Tipo {elementType} non supportato per Fast-Path.");
    }

    private int DetermineOptimalDepth(Array rawPixels)
    {
        return rawPixels switch
        {
            int[,] or uint[,] or double[,] => 64, 
            _ => 32 
        };
    }

    // =======================================================================
    // 2. SCRITTURA (OPENCV -> RAW) - SAFE IMPLEMENTATION
    // =======================================================================

    public Array MatToRaw(Mat mat, FitsBitDepth targetDepth)
    {
        if (mat.Empty()) return Array.CreateInstance(typeof(byte), 0, 0);

        int h = mat.Rows;
        int w = mat.Cols;

        // 1. Determina il tipo OpenCV necessario
        MatType requiredType = targetDepth switch
        {
            FitsBitDepth.UInt8 => MatType.CV_8UC1,
            FitsBitDepth.Int16 => MatType.CV_16SC1,
            FitsBitDepth.Int32 => MatType.CV_32SC1,
            FitsBitDepth.Float => MatType.CV_32FC1,
            FitsBitDepth.Double => MatType.CV_64FC1,
            _ => MatType.CV_32FC1
        };

        // 2. Converti se necessario
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
            // 3. Copia SAFE (usando Marshal.Copy e Buffer.BlockCopy)
            return targetDepth switch
            {
                FitsBitDepth.UInt8 => MatTo2DArrayByte(sourceToRead, h, w),
                FitsBitDepth.Int16 => MatTo2DArrayShort(sourceToRead, h, w),
                FitsBitDepth.Int32 => MatTo2DArrayInt(sourceToRead, h, w),
                FitsBitDepth.Float => MatTo2DArrayFloat(sourceToRead, h, w),
                FitsBitDepth.Double => MatTo2DArrayDouble(sourceToRead, h, w),
                _ => throw new NotSupportedException($"Export depth {targetDepth} non supportata")
            };
        }
        finally
        {
            if (tempCreated) sourceToRead.Dispose();
        }
    }

    // --- Metodi Tipizzati per evitare 'unsafe' ---

    private byte[,] MatTo2DArrayByte(Mat mat, int rows, int cols)
    {
        byte[,] result = new byte[rows, cols];
        byte[] rowBuffer = new byte[cols];
        
        for (int y = 0; y < rows; y++)
        {
            // Copia da IntPtr (OpenCV) a Array Managed Temporaneo
            Marshal.Copy(mat.Ptr(y), rowBuffer, 0, cols);
            // Copia veloce da Array a Array (Buffer.BlockCopy gestisce anche i 2D)
            Buffer.BlockCopy(rowBuffer, 0, result, y * cols, cols);
        }
        return result;
    }

    private short[,] MatTo2DArrayShort(Mat mat, int rows, int cols)
    {
        short[,] result = new short[rows, cols];
        short[] rowBuffer = new short[cols];
        int bytesPerRow = cols * 2; // sizeof(short)

        for (int y = 0; y < rows; y++)
        {
            Marshal.Copy(mat.Ptr(y), rowBuffer, 0, cols);
            Buffer.BlockCopy(rowBuffer, 0, result, y * bytesPerRow, bytesPerRow);
        }
        return result;
    }

    private int[,] MatTo2DArrayInt(Mat mat, int rows, int cols)
    {
        int[,] result = new int[rows, cols];
        int[] rowBuffer = new int[cols];
        int bytesPerRow = cols * 4; // sizeof(int)

        for (int y = 0; y < rows; y++)
        {
            Marshal.Copy(mat.Ptr(y), rowBuffer, 0, cols);
            Buffer.BlockCopy(rowBuffer, 0, result, y * bytesPerRow, bytesPerRow);
        }
        return result;
    }

    private float[,] MatTo2DArrayFloat(Mat mat, int rows, int cols)
    {
        float[,] result = new float[rows, cols];
        float[] rowBuffer = new float[cols];
        int bytesPerRow = cols * 4; // sizeof(float)

        for (int y = 0; y < rows; y++)
        {
            Marshal.Copy(mat.Ptr(y), rowBuffer, 0, cols);
            Buffer.BlockCopy(rowBuffer, 0, result, y * bytesPerRow, bytesPerRow);
        }
        return result;
    }

    private double[,] MatTo2DArrayDouble(Mat mat, int rows, int cols)
    {
        double[,] result = new double[rows, cols];
        double[] rowBuffer = new double[cols];
        int bytesPerRow = cols * 8; // sizeof(double)

        for (int y = 0; y < rows; y++)
        {
            Marshal.Copy(mat.Ptr(y), rowBuffer, 0, cols);
            Buffer.BlockCopy(rowBuffer, 0, result, y * bytesPerRow, bytesPerRow);
        }
        return result;
    }
}