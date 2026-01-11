using System;
using System.Runtime.InteropServices;
using OpenCvSharp;
using KomaLab.Models.Fits;
using nom.tam.fits;

namespace KomaLab.Services.Data;

// ---------------------------------------------------------------------------
// FILE: FitsImageDataConverter.cs
// RUOLO: Convertitore Dati (Pixel Engine)
// DESCRIZIONE:
// Gestisce la traduzione ad alte prestazioni tra Array C# e Matrici OpenCV.
// Implementa la logica POLIMORFICA: adatta il formato FITS di uscita (BITPIX)
// al tipo di dati richiesto (FitsBitDepth).
// ---------------------------------------------------------------------------

public class FitsImageDataConverter : IFitsImageDataConverter
{
    // =======================================================================
    // 1. RAW (FITS) -> MAT (OPENCV)
    // =======================================================================

    public Mat RawToMat(FitsImageData fitsData) 
    {
        if (fitsData == null) throw new ArgumentNullException(nameof(fitsData));
        return RawToMatRect(fitsData, 0, fitsData.Height);
    }
    
    public Mat RawToMatRect(FitsImageData fitsData, int yStart, int rowsToRead)
    {
        if (fitsData.RawData == null) throw new ArgumentNullException(nameof(fitsData));
        
        if (yStart < 0 || rowsToRead <= 0 || yStart + rowsToRead > fitsData.Height)
            throw new ArgumentOutOfRangeException("Richiesta regione fuori dai limiti.");

        var rawJaggedData = (Array[])fitsData.RawData;
        int width = fitsData.Width;
        
        int bitpix = fitsData.FitsHeader.GetIntValue("BITPIX");
        double bscale = fitsData.FitsHeader.GetDoubleValue("BSCALE", 1.0);
        double bzero = fitsData.FitsHeader.GetDoubleValue("BZERO", 0.0);

        // Per l'analisi scientifica interna usiamo sempre Double (massima precisione)
        Mat stripMat = new Mat(rowsToRead, width, MatType.CV_64FC1);

        try
        {
            switch (bitpix)
            {
                case 8:  CopyRegionToMat<byte>(rawJaggedData, stripMat, MatType.CV_8UC1, bscale, bzero, yStart); break;
                case 16: CopyRegionToMat<short>(rawJaggedData, stripMat, MatType.CV_16SC1, bscale, bzero, yStart); break;
                case 32: CopyRegionToMat<int>(rawJaggedData, stripMat, MatType.CV_32SC1, bscale, bzero, yStart); break;
                case -32: CopyRegionToMat<float>(rawJaggedData, stripMat, MatType.CV_32FC1, bscale, bzero, yStart); break;
                case -64: CopyRegionToMat<double>(rawJaggedData, stripMat, MatType.CV_64FC1, bscale, bzero, yStart); break;
                default: throw new NotSupportedException($"BITPIX {bitpix} non supportato.");
            }
        }
        catch
        {
            stripMat.Dispose();
            throw;
        }

        return stripMat;
    }
    
    // Helper per copiare DA Array Managed A Matrice Unmanaged
    private void CopyRegionToMat<T>(Array[] source, Mat destDouble, MatType tempType, double bscale, double bzero, int ySourceStart) 
        where T : struct
    {
        int h = destDouble.Rows; 
        int w = destDouble.Cols;

        using Mat tempMat = new Mat(h, w, tempType);

        for (int y = 0; y < h; y++)
        {
            T[] row = (T[])source[y + ySourceStart];
            IntPtr rowPtr = tempMat.Ptr(y);
            // Copia Array -> Ptr
            MarshalCopyArrayToPtr(row, 0, rowPtr, w);
        }

        // Conversione finale in Double applicando BSCALE/BZERO
        tempMat.ConvertTo(destDouble, MatType.CV_64FC1, bscale, bzero);
    }

    // =======================================================================
    // 2. MAT (OPENCV) -> RAW (FITS) - POLIMORFICO
    // =======================================================================

    public FitsImageData MatToFitsData(Mat mat, FitsBitDepth targetDepth = FitsBitDepth.Double)
    {
        if (mat == null) throw new ArgumentNullException(nameof(mat));
        if (mat.Empty()) throw new ArgumentException("Matrice vuota", nameof(mat));

        int w = mat.Width;
        int h = mat.Height;
        Array rawData;
        
        // Determina il tipo OpenCV richiesto in base all'Enum
        MatType requiredType = targetDepth switch
        {
            FitsBitDepth.UInt8  => MatType.CV_8UC1,
            FitsBitDepth.Int16  => MatType.CV_16SC1,
            FitsBitDepth.Int32  => MatType.CV_32SC1,
            FitsBitDepth.Float  => MatType.CV_32FC1,
            _ => MatType.CV_64FC1
        };

        // Se la matrice in input non è del tipo richiesto, convertiamola
        bool needsConversion = mat.Type() != requiredType;
        Mat sourceToRead = mat;
        
        if (needsConversion)
        {
            sourceToRead = new Mat();
            mat.ConvertTo(sourceToRead, requiredType);
        }

        try
        {
            // Estrazione tipizzata: Qui invochiamo il metodo che mancava!
            switch (targetDepth)
            {
                case FitsBitDepth.UInt8:
                    rawData = MatToJaggedArray<byte>(sourceToRead, h, w);
                    break;
                case FitsBitDepth.Int16:
                    rawData = MatToJaggedArray<short>(sourceToRead, h, w);
                    break;
                case FitsBitDepth.Int32:
                    rawData = MatToJaggedArray<int>(sourceToRead, h, w);
                    break;
                case FitsBitDepth.Float:
                    rawData = MatToJaggedArray<float>(sourceToRead, h, w);
                    break;
                case FitsBitDepth.Double:
                default:
                    rawData = MatToJaggedArray<double>(sourceToRead, h, w);
                    break;
            }

            // Creazione Header Strutturale
            var newHeader = new Header();
            newHeader.AddValue("SIMPLE", true, "Standard FITS format");
            newHeader.AddValue("BITPIX", (int)targetDepth, GetBitpixComment((int)targetDepth));
            newHeader.AddValue("NAXIS", 2, "2D Image");
            newHeader.AddValue("BSCALE", 1.0, "Physical = Raw * BSCALE + BZERO");
            newHeader.AddValue("BZERO", 0.0, null);

            return new FitsImageData
            {
                RawData = rawData,
                FitsHeader = newHeader,
                Width = w,
                Height = h
            };
        }
        finally
        {
            if (needsConversion) sourceToRead.Dispose();
        }
    }

    // =======================================================================
    // 3. HELPERS DI MARSHALLING (ECCO IL CODICE MANCANTE)
    // =======================================================================

    /// <summary>
    /// Converte una Matrice OpenCV in un Jagged Array C# (T[][])
    /// </summary>
    private T[][] MatToJaggedArray<T>(Mat mat, int rows, int cols) where T : struct
    {
        T[][] jagged = new T[rows][];
        
        for (int y = 0; y < rows; y++)
        {
            jagged[y] = new T[cols];
            // Legge il puntatore alla riga OpenCV
            IntPtr srcPtr = mat.Ptr(y);
            // Copia Ptr -> Array Managed
            MarshalCopyPtrToArray(srcPtr, jagged[y], cols);
        }
        return jagged;
    }

    /// <summary>
    /// Helper per copiare DA un Puntatore Unmanaged A un Array Managed.
    /// (Direzione: OpenCV -> C#)
    /// </summary>
    private void MarshalCopyPtrToArray<T>(IntPtr sourcePtr, T[] destArray, int length) where T : struct
    {
        if (destArray is byte[] b) Marshal.Copy(sourcePtr, b, 0, length);
        else if (destArray is short[] s) Marshal.Copy(sourcePtr, s, 0, length);
        else if (destArray is int[] i) Marshal.Copy(sourcePtr, i, 0, length);
        else if (destArray is float[] f) Marshal.Copy(sourcePtr, f, 0, length);
        else if (destArray is double[] d) Marshal.Copy(sourcePtr, d, 0, length);
        else throw new NotSupportedException($"Tipo {typeof(T)} non supportato per output FITS.");
    }

    /// <summary>
    /// Helper per copiare DA un Array Managed A un Puntatore Unmanaged.
    /// (Direzione: C# -> OpenCV)
    /// </summary>
    private void MarshalCopyArrayToPtr<T>(T[] sourceArray, int startIndex, IntPtr destinationPtr, int length) 
        where T : struct
    {
        if (sourceArray is byte[] b) Marshal.Copy(b, startIndex, destinationPtr, length);
        else if (sourceArray is short[] s) Marshal.Copy(s, startIndex, destinationPtr, length);
        else if (sourceArray is int[] i) Marshal.Copy(i, startIndex, destinationPtr, length);
        else if (sourceArray is float[] f) Marshal.Copy(f, startIndex, destinationPtr, length);
        else if (sourceArray is double[] d) Marshal.Copy(d, startIndex, destinationPtr, length);
        else throw new NotSupportedException($"Tipo {typeof(T)} non supportato per input FITS.");
    }
    
    private string GetBitpixComment(int bitpix) => bitpix switch
    {
        8 => "8-bit Unsigned Integer",
        16 => "16-bit Integer",
        32 => "32-bit Integer",
        -32 => "Single Precision Float",
        -64 => "Double Precision Float",
        _ => ""
    };
}