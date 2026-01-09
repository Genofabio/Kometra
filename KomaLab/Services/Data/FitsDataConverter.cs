using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using KomaLab.Models;
using KomaLab.Models.Fits;
using MathNet.Numerics.Statistics;
using nom.tam.fits;
using OpenCvSharp;

namespace KomaLab.Services.Data;

public class FitsDataConverter : IFitsDataConverter
{
    public Mat RawToMat(FitsImageData fitsData) 
    {
        return RawToMatRect(fitsData, 0, fitsData.Height);
    }
    
    public Mat RawToMatRect(FitsImageData? fitsData, int yStart, int rowsToRead)
    {
        if (fitsData?.RawData == null) throw new ArgumentNullException(nameof(fitsData));

        var rawJaggedData = (Array[])fitsData.RawData;
        int width = fitsData.Width;
        int bitpix = fitsData.FitsHeader.GetIntValue("BITPIX");
        double bscale = fitsData.FitsHeader.GetDoubleValue("BSCALE", 1.0);
        double bzero = fitsData.FitsHeader.GetDoubleValue("BZERO", 0.0);

        // La matrice di destinazione (Double)
        Mat stripMat = new Mat(rowsToRead, width, MatType.CV_64FC1);

        try
        {
            // Nota: Qui usiamo i tipi C# corrispondenti a BITPIX standard
            switch (bitpix)
            {
                case 8:  CopyRegionToMat<byte>(rawJaggedData, stripMat, MatType.CV_8UC1, bscale, bzero, yStart); break;
                case 16: CopyRegionToMat<short>(rawJaggedData, stripMat, MatType.CV_16SC1, bscale, bzero, yStart); break;
                case 32: CopyRegionToMat<int>(rawJaggedData, stripMat, MatType.CV_32SC1, bscale, bzero, yStart); break;
                case -32: CopyRegionToMat<float>(rawJaggedData, stripMat, MatType.CV_32FC1, bscale, bzero, yStart); break;
                case -64: CopyRegionToMat<double>(rawJaggedData, stripMat, MatType.CV_64FC1, bscale, bzero, yStart); break;
                default: throw new NotSupportedException($"BITPIX {bitpix} not supported.");
            }
        }
        catch
        {
            stripMat.Dispose();
            throw;
        }

        return stripMat;
    }
    
    private void CopyRegionToMat<T>(Array[] source, Mat destDouble, MatType tempType, double bscale, double bzero, int ySourceStart) 
        where T : struct
    {
        int h = destDouble.Rows; 
        int w = destDouble.Cols;

        // 1. Crea una matrice temporanea del tipo nativo (es. Short)
        using Mat tempMat = new Mat(h, w, tempType);

        // 2. Copia riga per riga da Array C# -> Mat Nativa (Veloce con Marshal)
        for (int y = 0; y < h; y++)
        {
            T[] row = (T[])source[y + ySourceStart];
            
            // Ottieni puntatore alla riga Y della matrice OpenCV
            IntPtr rowPtr = tempMat.Ptr(y);
            
            // Copia brutale di memoria (Fulmineo)
            MarshalCopy(row, 0, rowPtr, w);
        }

        // 3. Conversione finale ottimizzata da OpenCV (gestisce bscale/bzero con SIMD)
        tempMat.ConvertTo(destDouble, MatType.CV_64FC1, bscale, bzero);
    }

    // --- OTTIMIZZAZIONE MASSIVA QUI ---
    public FitsImageData MatToFitsData(Mat mat, FitsImageData? originalTemplate)
    {
        // 1. Assicuriamo formato Double
        Mat source = mat;
        bool weCreatedTemp = false;

        if (mat.Type() != MatType.CV_64FC1)
        {
            source = new Mat();
            mat.ConvertTo(source, MatType.CV_64FC1);
            weCreatedTemp = true;
        }

        try
        {
            int w = source.Width;
            int h = source.Height;
            
            // 2. Ricostruiamo il jagged array
            double[][] jaggedData = new double[h][];

            // 3. COPY MEMORY VELOCE (Rimossa logica GetGenericIndexer)
            // Copiamo riga per riga usando Marshal.Copy al contrario (IntPtr -> Array)
            for (int y = 0; y < h; y++)
            {
                jaggedData[y] = new double[w];
                IntPtr rowPtr = source.Ptr(y);
                
                // Copia diretta da Memoria Non Gestita (OpenCV) a Memoria Gestita (C# Array)
                Marshal.Copy(rowPtr, jaggedData[y], 0, w);
            }

            // 4. Creazione Header (Codice originale preservato)
            var newHeader = RebuildCleanHeader(originalTemplate);

            return new FitsImageData
            {
                RawData = jaggedData,
                FitsHeader = newHeader,
                Width = w,
                Height = h
            };
        }
        finally
        {
            // Pulizia se abbiamo creato una matrice temporanea per la conversione
            if (weCreatedTemp)
            {
                source.Dispose();
            }
        }
    }

    private Header RebuildCleanHeader(FitsImageData? originalTemplate)
    {
        var newHeader = new Header();
        var keysToNeutralize = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "BITPIX", "BSCALE", "BZERO", "DATAMIN", "DATAMAX"
        };

        if (originalTemplate?.FitsHeader != null)
        {
            var cursor = originalTemplate.FitsHeader.GetCursor();
            while (cursor.MoveNext())
            {
                if (cursor.Current is DictionaryEntry { Value: HeaderCard hc } && !keysToNeutralize.Contains(hc.Key))
                    newHeader.AddCard(hc);
                else if (cursor.Current is HeaderCard c && !keysToNeutralize.Contains(c.Key))
                    newHeader.AddCard(c);
            }
        }

        newHeader.AddValue("BITPIX", -64, "Double Precision");
        newHeader.AddValue("BSCALE", 1.0, null);
        newHeader.AddValue("BZERO", 0.0, null);
        return newHeader;
    }

    // Helper Generico per Marshal.Copy
    private void MarshalCopy<T>(T[] source, int startIndex, IntPtr destination, int length)
    {
        if (source is byte[] b) Marshal.Copy(b, startIndex, destination, length);
        else if (source is short[] s) Marshal.Copy(s, startIndex, destination, length);
        else if (source is int[] i) Marshal.Copy(i, startIndex, destination, length);
        else if (source is float[] f) Marshal.Copy(f, startIndex, destination, length);
        else if (source is double[] d) Marshal.Copy(d, startIndex, destination, length);
        else throw new NotSupportedException($"Type {typeof(T)} not supported for Marshal.Copy");
    }

    public (double Black, double White) CalculateDisplayThresholds(FitsImageData data)
    {
        if (data.RawData is not Array[] jagged) return (0, 255);
        
        if (data.FitsHeader.GetIntValue("BITPIX") == 8)
        {
            return (0, 255);
        }
        
        int bitpix = data.FitsHeader.GetIntValue("BITPIX");
        double bscale = data.FitsHeader.GetDoubleValue("BSCALE", 1.0);
        double bzero = data.FitsHeader.GetDoubleValue("BZERO", 0.0);
        
        int maxSamples = 10000;
        var samples = new List<double>(maxSamples);
        long totalPixels = (long)data.Width * data.Height;
        double step = Math.Max(1.0, totalPixels / (double)maxSamples);

        // Campionamento sparse per velocità
        for (int i = 0; i < maxSamples; i++)
        {
            long idx = (long)(i * step);
            if (idx >= totalPixels) break;
            
            int y = (int)(idx / data.Width);
            int x = (int)(idx % data.Width);

            try {
                double rawVal = GetValueFromJagged(jagged, y, x, bitpix);
                double physVal = (rawVal * bscale) + bzero;
                
                if (!double.IsNaN(physVal) && !double.IsInfinity(physVal))
                    samples.Add(physVal);
            }
            catch { /* Ignore bounds */ }
        }

        if (samples.Count == 0) return (0, 1);

        double b = Statistics.Quantile(samples, 0.3); 
        double w = Statistics.Quantile(samples, 0.995);
        
        if (Math.Abs(w - b) < 1e-6) w = b + 1.0;
        
        return (b, w);
    }

    private double GetValueFromJagged(Array[] jagged, int y, int x, int bitpix)
    {
        return bitpix switch
        {
            8 => ((byte[])jagged[y])[x],
            16 => ((short[])jagged[y])[x],
            32 => ((int[])jagged[y])[x],
            -32 => ((float[])jagged[y])[x],
            -64 => ((double[])jagged[y])[x],
            _ => 0.0
        };
    }
}