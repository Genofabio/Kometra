using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using KomaLab.Models;
using MathNet.Numerics.Statistics;
using nom.tam.fits;
using OpenCvSharp;

namespace KomaLab.Services;

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
        // L'altezza della matrice risultante sarà solo quella della striscia
        int bitpix = fitsData.FitsHeader.GetIntValue("BITPIX");
        double bscale = fitsData.FitsHeader.GetDoubleValue("BSCALE", 1.0);
        double bzero = fitsData.FitsHeader.GetDoubleValue("BZERO", 0.0);

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
        int h = destDouble.Rows; // Altezza della striscia
        int w = destDouble.Cols;

        using Mat tempMat = new Mat(h, w, tempType);

        for (int y = 0; y < h; y++)
        {
            // Leggiamo dall'array originale all'indice [y + offset]
            // Scriviamo nella matrice temporanea all'indice [y] (locale)
            T[] row = (T[])source[y + ySourceStart];
        
            IntPtr rowPtr = tempMat.Ptr(y);
            MarshalCopy(row, 0, rowPtr, w);
        }

        tempMat.ConvertTo(destDouble, MatType.CV_64FC1, bscale, bzero);
    }

    // Helper per gestire i diversi overload di Marshal.Copy tramite Generics (trucco C#)
    private void MarshalCopy<T>(T[] source, int startIndex, IntPtr destination, int length)
    {
        if (source is byte[] b) Marshal.Copy(b, startIndex, destination, length);
        else if (source is short[] s) Marshal.Copy(s, startIndex, destination, length);
        else if (source is int[] i) Marshal.Copy(i, startIndex, destination, length);
        else if (source is float[] f) Marshal.Copy(f, startIndex, destination, length);
        else if (source is double[] d) Marshal.Copy(d, startIndex, destination, length);
        else throw new NotSupportedException($"Type {typeof(T)} not supported for Marshal.Copy");
    }

    public FitsImageData MatToFitsData(Mat mat, FitsImageData? originalTemplate)
    {
        // Assicuriamoci che sia Double
        using Mat tempDouble = new Mat();
        Mat source = mat;

        if (mat.Type() != MatType.CV_64FC1)
        {
            mat.ConvertTo(tempDouble, MatType.CV_64FC1);
            source = tempDouble;
        }

        int w = source.Width;
        int h = source.Height;
        
        // Ricostruiamo il jagged array
        double[][] jaggedData = new double[h][];
        var indexer = source.GetGenericIndexer<double>();

        // Qui la copia manuale è inevitabile per creare la struttura jagged di C#
        // Parallelizzabile se necessario, ma di solito è veloce.
        for (int y = 0; y < h; y++)
        {
            jaggedData[y] = new double[w];
            for (int x = 0; x < w; x++)
            {
                jaggedData[y][x] = indexer[y, x];
            }
        }

        // Creazione Header pulito (logica conservata dal tuo codice originale)
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
                switch (cursor.Current)
                {
                    case DictionaryEntry { Value: HeaderCard hc }:
                    {
                        if (!keysToNeutralize.Contains(hc.Key)) newHeader.AddCard(hc);
                        break;
                    }
                    case HeaderCard c:
                    {
                        if (!keysToNeutralize.Contains(c.Key)) newHeader.AddCard(c);
                        break;
                    }
                }
            }
        }

        newHeader.AddValue("BITPIX", -64, "Double Precision");
        newHeader.AddValue("BSCALE", 1.0, null);
        newHeader.AddValue("BZERO", 0.0, null);

        return new FitsImageData
        {
            RawData = jaggedData,
            FitsHeader = newHeader,
            Width = w,
            Height = h
        };
    }

    public (double Black, double White) CalculateDisplayThresholds(FitsImageData data)
    {
        // Questo usa la TUA logica di sampling (eccellente per performance)
        // Non richiede allocazione di Mat.
        
        if (data.RawData is not Array[] jagged) return (0, 255);
        
        int bitpix = data.FitsHeader.GetIntValue("BITPIX");
        double bscale = data.FitsHeader.GetDoubleValue("BSCALE", 1.0);
        double bzero = data.FitsHeader.GetDoubleValue("BZERO", 0.0);
        
        // Campionamento intelligente
        int maxSamples = 10000;
        var samples = new List<double>(maxSamples);
        long totalPixels = (long)data.Width * data.Height;
        double step = Math.Max(1.0, totalPixels / (double)maxSamples);

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
            catch
            {
                // ignored
            }
        }

        if (samples.Count == 0) return (0, 1);

        double b = Statistics.Quantile(samples, 0.3); // Un po' più conservativo del 0.15
        double w = Statistics.Quantile(samples, 0.995);
        
        // Protezione contro immagini piatte
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