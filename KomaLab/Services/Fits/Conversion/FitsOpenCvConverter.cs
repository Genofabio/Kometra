using System;
using System.Diagnostics; // Necessario per i Print
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using KomaLab.Models.Fits;
using KomaLab.Models.Fits.Structure;
using OpenCvSharp;

namespace KomaLab.Services.Fits.Conversion;

public class FitsOpenCvConverter : IFitsOpenCvConverter
{
    public Mat RawToMat(Array rawPixels, double bScale = 1.0, double bZero = 0.0, FitsBitDepth? targetDepth = null)
    {
        if (rawPixels == null) throw new ArgumentNullException(nameof(rawPixels));
    
        int height = rawPixels.GetLength(0); 
        int width = rawPixels.GetLength(1);
        string typeName = rawPixels.GetType().Name;

        // --- INIZIO DIAGNOSTICA ---
        Debug.WriteLine($"\n[FitsConverter] --- RICHIESTA CONVERSIONE ---");
        Debug.WriteLine($"[FitsConverter] Tipo Matrice: {typeName}");
        Debug.WriteLine($"[FitsConverter] Dimensioni: {width} x {height}");
        Debug.WriteLine($"[FitsConverter] Parametri Header: BSCALE={bScale}, BZERO={bZero}");
        
        // Calcoliamo Min/Max dei dati GREZZI (C#) per vedere se arrivano giusti o corrotti
        // Lo facciamo solo per i tipi più comuni per non rallentare troppo
        if (rawPixels is float[,] fData)
        {
            float min = float.MaxValue, max = float.MinValue;
            // Campioniamo solo la riga centrale per velocità, o tutto se serve precisione assoluta
            // Qui analizzo TUTTO per essere sicuro al 100%
            bool hasNaN = false;
            foreach (var val in fData)
            {
                if (float.IsNaN(val)) hasNaN = true;
                else
                {
                    if (val < min) min = val;
                    if (val > max) max = val;
                }
            }
            Debug.WriteLine($"[FitsConverter] ANALISI DATI RAW (Float):");
            Debug.WriteLine($"   -> Minimo Reale: {min}");
            Debug.WriteLine($"   -> Massimo Reale: {max}");
            Debug.WriteLine($"   -> Contiene NaN? {hasNaN}");
        }
        else if (rawPixels is short[,] sData)
        {
            short min = sData[0,0], max = sData[0,0];
            foreach(var val in sData) { if(val < min) min = val; if(val > max) max = val; }
            Debug.WriteLine($"[FitsConverter] ANALISI DATI RAW (Short): Min={min}, Max={max}");
        }
        else if (rawPixels is byte[,] bData)
        {
             Debug.WriteLine($"[FitsConverter] ANALISI DATI RAW (Byte): Analisi saltata (è byte).");
        }
        // ---------------------------

        if (height == 0 || width == 0) return new Mat();
    
        int depth;
        if (targetDepth.HasValue)
        {
            depth = (targetDepth.Value == FitsBitDepth.Float) ? 32 : 64;
        }
        else
        {
            depth = DetermineOptimalDepth(rawPixels);
        }
    
        // FIX VISUALIZZAZIONE CCD:
        if (rawPixels is ushort[,] || rawPixels is uint[,])
        {
            Debug.WriteLine($"[FitsConverter] Rilevato Unsigned. Forzo BZero a 0.0 (era {bZero}).");
            bZero = 0.0; 
        }

        Debug.WriteLine($"[FitsConverter] Conversione OpenCV avviata. TargetDepth={depth} bit.");
        var result = RawToMatRect(rawPixels, 0, height, bScale, bZero, depth);
        
        // Verifica finale post-conversione
        result.MinMaxLoc(out double finalMin, out double finalMax);
        Debug.WriteLine($"[FitsConverter] RISULTATO OPENCV -> Min: {finalMin}, Max: {finalMax}");
        Debug.WriteLine($"[FitsConverter] -----------------------------------\n");

        return result;
    }

    private int DetermineOptimalDepth(Array rawPixels)
    {
        return rawPixels switch
        {
            byte[,] or sbyte[,] => 32,
            short[,] or ushort[,] => 32,
            int[,] or uint[,] => 64,
            float[,] => 32,
            double[,] => 64,
            _ => 64
        };
    }

    private Mat RawToMatRect(Array rawPixels, int yStart, int rowsToRead, double bScale = 1.0, double bZero = 0.0, int targetBitDepth = 64)
    {
        int totalHeight = rawPixels.GetLength(0);
        int width = rawPixels.GetLength(1);

        if (totalHeight == 0 || width == 0) return new Mat();

        if (yStart < 0 || rowsToRead <= 0 || yStart + rowsToRead > totalHeight)
            throw new ArgumentOutOfRangeException(nameof(yStart), $"Regione non valida: Start={yStart}, Rows={rowsToRead}, H={totalHeight}");

        MatType destinationType = (targetBitDepth == 32) ? MatType.CV_32FC1 : MatType.CV_64FC1;
        Mat stripMat = new Mat(rowsToRead, width, destinationType);

        try
        {
            switch (rawPixels)
            {
                case byte[,] b:   Copy2DToMat(b, stripMat, MatType.CV_8UC1, bScale, bZero, yStart); break;
                case sbyte[,] sb: Copy2DToMat(sb, stripMat, MatType.CV_8SC1, bScale, bZero, yStart); break;
                
                case short[,] s:  Copy2DToMat(s, stripMat, MatType.CV_16SC1, bScale, bZero, yStart); break;
                case ushort[,] us: Copy2DToMat(us, stripMat, MatType.CV_16UC1, bScale, bZero, yStart); break;
                
                case int[,] i:    Copy2DToMat(i, stripMat, MatType.CV_32SC1, bScale, bZero, yStart); break;
                case uint[,] ui:  Copy2DToMat(ui, stripMat, MatType.CV_32SC1, bScale, bZero, yStart); break;
                
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

    private void Copy2DToMat<T>(T[,] source, Mat destMat, MatType sourceTempType, double bScale, double bZero, int yStart) where T : struct
    {
        int h = destMat.Rows;
        int w = destMat.Cols;
        
        using Mat tempMat = new Mat(h, w, sourceTempType);
        T[] rowBuffer = new T[w]; 

        for (int y = 0; y < h; y++)
        {
            IntPtr destPtr = tempMat.Ptr(y);
            for (int x = 0; x < w; x++) rowBuffer[x] = source[y + yStart, x];
            MarshalCopyArrayToPtr(rowBuffer, 0, destPtr, w);
        }
        
        tempMat.ConvertTo(destMat, destMat.Type(), bScale, bZero);
    }

    public Array MatToRaw(Mat mat, FitsBitDepth targetDepth = FitsBitDepth.Double)
    {
        if (mat.Empty()) throw new ArgumentException("Matrice nulla o vuota", nameof(mat));
        int w = mat.Width; int h = mat.Height;
        
        MatType requiredType = targetDepth switch {
            FitsBitDepth.UInt8 => MatType.CV_8UC1, 
            FitsBitDepth.Int16 => MatType.CV_16SC1,
            FitsBitDepth.Int32 => MatType.CV_32SC1, 
            FitsBitDepth.Float => MatType.CV_32FC1, 
            _ => MatType.CV_64FC1 
        };

        Mat sourceToRead = mat;
        bool tempCreated = false;
        
        if (mat.Type() != requiredType) { 
            sourceToRead = new Mat(); 
            mat.ConvertTo(sourceToRead, requiredType); 
            tempCreated = true; 
        }

        try {
            return targetDepth switch {
                FitsBitDepth.UInt8 => MatTo2DArray<byte>(sourceToRead, h, w), 
                FitsBitDepth.Int16 => MatTo2DArray<short>(sourceToRead, h, w),
                FitsBitDepth.Int32 => MatTo2DArray<int>(sourceToRead, h, w), 
                FitsBitDepth.Float => MatTo2DArray<float>(sourceToRead, h, w),
                _ => MatTo2DArray<double>(sourceToRead, h, w),
            };
        } finally { if (tempCreated) sourceToRead.Dispose(); }
    }

    private T[,] MatTo2DArray<T>(Mat mat, int rows, int cols) where T : struct {
        T[,] result = new T[rows, cols]; 
        T[] rowBuffer = new T[cols];
        for (int y = 0; y < rows; y++) { 
            IntPtr srcPtr = mat.Ptr(y); 
            MarshalCopyPtrToArray(srcPtr, rowBuffer, cols); 
            for (int x = 0; x < cols; x++) result[y, x] = rowBuffer[x]; 
        }
        return result;
    }

    private void MarshalCopyPtrToArray<T>(IntPtr sourcePtr, T[] destArray, int length) where T : struct {
        if (destArray is byte[] b) Marshal.Copy(sourcePtr, b, 0, length);
        else if (destArray is short[] s) Marshal.Copy(sourcePtr, s, 0, length);
        else if (destArray is int[] i) Marshal.Copy(sourcePtr, i, 0, length);
        else if (destArray is float[] f) Marshal.Copy(sourcePtr, f, 0, length);
        else if (destArray is double[] d) Marshal.Copy(sourcePtr, d, 0, length);
        else if (destArray is ushort[] us)
        {
            short[] tempS = new short[length];
            Marshal.Copy(sourcePtr, tempS, 0, length);
            Buffer.BlockCopy(tempS, 0, us, 0, length * 2);
        }
    }

    private void MarshalCopyArrayToPtr<T>(T[] sourceArray, int startIndex, IntPtr destinationPtr, int length) where T : struct {
        if (sourceArray is byte[] b) Marshal.Copy(b, startIndex, destinationPtr, length);
        else if (sourceArray is short[] s) Marshal.Copy(s, startIndex, destinationPtr, length);
        else if (sourceArray is int[] i) Marshal.Copy(i, startIndex, destinationPtr, length);
        else if (sourceArray is float[] f) Marshal.Copy(f, startIndex, destinationPtr, length);
        else if (sourceArray is double[] d) Marshal.Copy(d, startIndex, destinationPtr, length);
        else if (sourceArray is sbyte[] sb) 
        {
            var asByte = Unsafe.As<sbyte[], byte[]>(ref sb);
            Marshal.Copy(asByte, startIndex, destinationPtr, length);
        }
        else if (sourceArray is ushort[] us) 
        {
            var asShort = Unsafe.As<ushort[], short[]>(ref us);
            Marshal.Copy(asShort, startIndex, destinationPtr, length);
        }
        else if (sourceArray is uint[] ui) 
        {
            var asInt = Unsafe.As<uint[], int[]>(ref ui);
            Marshal.Copy(asInt, startIndex, destinationPtr, length);
        }
        else throw new NotSupportedException($"MarshalCopyArrayToPtr non supporta il tipo {typeof(T)}");
    }
}