using System;
using System.Runtime.InteropServices;
using Kometra.Models.Fits.Structure;
using OpenCvSharp;

namespace Kometra.Services.Fits.Conversion;

public class FitsOpenCvConverter : IFitsOpenCvConverter
{
    // =======================================================================
    // 1. LETTURA (RAW -> OPENCV)
    // =======================================================================

    public Mat RawToMat(Array rawPixels, double bScale = 1.0, double bZero = 0.0, FitsBitDepth? targetDepth = null)
    {
        Console.WriteLine("[Converter] Entrato in RawToMat.");
        if (rawPixels == null) throw new ArgumentNullException(nameof(rawPixels));

        int rows = rawPixels.GetLength(0);
        int cols = rawPixels.GetLength(1);
        Console.WriteLine($"[Converter] Dimensioni rilevate: {cols}x{rows}");

        if (rows == 0 || cols == 0) 
        {
            Console.WriteLine("[Converter] Immagine vuota, ritorno Mat vuota.");
            return new Mat();
        }

        int depthBit = targetDepth.HasValue 
            ? (targetDepth.Value == FitsBitDepth.Float ? 32 : 64) 
            : DetermineOptimalDepth(rawPixels);
            
        MatType destType = (depthBit == 32) ? MatType.CV_32FC1 : MatType.CV_64FC1;
        Console.WriteLine($"[Converter] Target Depth: {depthBit}-bit ({destType})");

        if (rawPixels is ushort[,] || rawPixels is uint[,]) bZero = 0.0;

        Type elementType = rawPixels.GetType().GetElementType();
        Console.WriteLine($"[Converter] Tipo elemento sorgente: {elementType?.Name}");

        // =========================================================
        // FIX PER IL TIPO UINT (Mancanza di CV_32UC1 in OpenCV)
        // =========================================================
        if (elementType == typeof(uint))
        {
            Console.WriteLine("[Converter] Rilevato uint[,], avvio Safe-Path (ConvertUIntToMatSafe)...");
            return ConvertUIntToMatSafe((uint[,])rawPixels, destType, bScale, bZero);
        }

        // =========================================================
        // FAST PATH STANDARD (Zero-Copy) PER TUTTI GLI ALTRI TIPI
        // =========================================================
        Console.WriteLine("[Converter] Avvio Fast-Path (Pinning memoria C#)...");
        GCHandle handle = GCHandle.Alloc(rawPixels, GCHandleType.Pinned);

        try
        {
            IntPtr rawDataPtr = handle.AddrOfPinnedObject();
            Console.WriteLine($"[Converter] Memoria pinnata a indirizzo: {rawDataPtr}");

            // Creiamo una vista OpenCV sui dati C#
            Console.WriteLine("[Converter] Chiamata a CreateWrapper...");
            using Mat rawWrapper = CreateWrapper(rows, cols, elementType, rawDataPtr);
            Console.WriteLine("[Converter] Wrapper creato correttamente.");

            // ConvertTo applica matematicamente bScale e bZero in modo corretto
            Console.WriteLine($"[Converter] Chiamata a ConvertTo (Scale: {bScale}, Zero: {bZero})...");
            Mat result = new Mat(rows, cols, destType);
            rawWrapper.ConvertTo(result, destType, bScale, bZero);
            Console.WriteLine("[Converter] ConvertTo completato.");

            return result;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Converter FATAL] Eccezione durante il Fast-Path: {ex.Message}");
            throw;
        }
        finally
        {
            if (handle.IsAllocated) 
            {
                handle.Free();
                Console.WriteLine("[Converter] Memoria C# sbloccata (handle.Free).");
            }
        }
    }

    private Mat CreateWrapper(int rows, int cols, Type elementType, IntPtr ptr)
    {
        Console.WriteLine($"[Converter] CreateWrapper per tipo {elementType?.Name}...");
        
        // Ogni chiamata a Mat.FromPixelData è un punto di ingresso nella libreria nativa
        if (elementType == typeof(short)) {
            Console.WriteLine("[Converter] Invocazione Mat.FromPixelData (CV_16SC1)");
            return Mat.FromPixelData(rows, cols, MatType.CV_16SC1, ptr);
        }
        if (elementType == typeof(ushort)) {
            Console.WriteLine("[Converter] Invocazione Mat.FromPixelData (CV_16UC1)");
            return Mat.FromPixelData(rows, cols, MatType.CV_16UC1, ptr);
        }
        if (elementType == typeof(byte)) {
            Console.WriteLine("[Converter] Invocazione Mat.FromPixelData (CV_8UC1)");
            return Mat.FromPixelData(rows, cols, MatType.CV_8UC1, ptr);
        }
        if (elementType == typeof(sbyte)) {
            Console.WriteLine("[Converter] Invocazione Mat.FromPixelData (CV_8SC1)");
            return Mat.FromPixelData(rows, cols, MatType.CV_8SC1, ptr);
        }
        if (elementType == typeof(int)) {
            Console.WriteLine("[Converter] Invocazione Mat.FromPixelData (CV_32SC1)");
            return Mat.FromPixelData(rows, cols, MatType.CV_32SC1, ptr);
        }
        if (elementType == typeof(float)) {
            Console.WriteLine("[Converter] Invocazione Mat.FromPixelData (CV_32FC1)");
            return Mat.FromPixelData(rows, cols, MatType.CV_32FC1, ptr);
        }
        if (elementType == typeof(double)) {
            Console.WriteLine("[Converter] Invocazione Mat.FromPixelData (CV_64FC1)");
            return Mat.FromPixelData(rows, cols, MatType.CV_64FC1, ptr);
        }

        throw new NotSupportedException($"Tipo {elementType} non supportato.");
    }

    private Mat ConvertUIntToMatSafe(uint[,] source, MatType destType, double bScale, double bZero)
    {
        int rows = source.GetLength(0);
        int cols = source.GetLength(1);
        Mat result = new Mat(rows, cols, destType);
        bool isFloat = destType == MatType.CV_32FC1;

        if (isFloat)
        {
            float[] rowBuffer = new float[cols];
            for (int y = 0; y < rows; y++)
            {
                for (int x = 0; x < cols; x++)
                {
                    rowBuffer[x] = (float)(source[y, x] * bScale + bZero);
                }
                Marshal.Copy(rowBuffer, 0, result.Ptr(y), cols);
            }
        }
        else
        {
            double[] rowBuffer = new double[cols];
            for (int y = 0; y < rows; y++)
            {
                for (int x = 0; x < cols; x++)
                {
                    rowBuffer[x] = source[y, x] * bScale + bZero;
                }
                Marshal.Copy(rowBuffer, 0, result.Ptr(y), cols);
            }
        }

        return result;
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

        MatType requiredType = targetDepth switch
        {
            FitsBitDepth.UInt8 => MatType.CV_8UC1,
            FitsBitDepth.Int16 => MatType.CV_16SC1,
            FitsBitDepth.Int32 => MatType.CV_32SC1,
            FitsBitDepth.Float => MatType.CV_32FC1,
            FitsBitDepth.Double => MatType.CV_64FC1,
            _ => MatType.CV_32FC1
        };

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

    private byte[,] MatTo2DArrayByte(Mat mat, int rows, int cols)
    {
        byte[,] result = new byte[rows, cols];
        byte[] rowBuffer = new byte[cols];
        for (int y = 0; y < rows; y++)
        {
            Marshal.Copy(mat.Ptr(y), rowBuffer, 0, cols);
            Buffer.BlockCopy(rowBuffer, 0, result, y * cols, cols);
        }
        return result;
    }

    private short[,] MatTo2DArrayShort(Mat mat, int rows, int cols)
    {
        short[,] result = new short[rows, cols];
        short[] rowBuffer = new short[cols];
        int bytesPerRow = cols * 2; 
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
        int bytesPerRow = cols * 4; 
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
        int bytesPerRow = cols * 4; 
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
        int bytesPerRow = cols * 8; 
        for (int y = 0; y < rows; y++)
        {
            Marshal.Copy(mat.Ptr(y), rowBuffer, 0, cols);
            Buffer.BlockCopy(rowBuffer, 0, result, y * bytesPerRow, bytesPerRow);
        }
        return result;
    }
}