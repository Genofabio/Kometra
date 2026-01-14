using System;
using System.Runtime.InteropServices;
using KomaLab.Models.Fits;
using KomaLab.Models.Fits; // Assicurati che FitsCard sia visibile (namespace appiattito)
using OpenCvSharp;

namespace KomaLab.Services.Fits;

// ---------------------------------------------------------------------------
// FILE: FitsImageDataConverter.cs
// RUOLO: Convertitore Dati (Pixel Engine)
// DESCRIZIONE:
// Gestisce la traduzione ad alte prestazioni tra Array C# (T[,]) e Matrici OpenCV.
// Aggiornato per supportare array multidimensionali nativi e Deep Copy dell'Header.
// ---------------------------------------------------------------------------

public class FitsImageDataConverter : IFitsImageDataConverter
{
    // =======================================================================
    // 1. RAW (FITS T[,]) -> MAT (OPENCV)
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

        int width = fitsData.Width;
        
        // Uso del nuovo Header Core per leggere i metadati
        int bitpix = fitsData.FitsHeader.GetIntValue("BITPIX");
        
        // Uso del null-coalescing operator (??) per gestire i double nullable
        double bscale = fitsData.FitsHeader.GetValue<double>("BSCALE") ?? 1.0;
        double bzero = fitsData.FitsHeader.GetValue<double>("BZERO") ?? 0.0;

        // Matrice di destinazione (sempre Double per precisione scientifica)
        Mat stripMat = new Mat(rowsToRead, width, MatType.CV_64FC1);

        try
        {
            switch (bitpix)
            {
                case 8:  Copy2DToMat<byte>(fitsData.RawData, stripMat, MatType.CV_8UC1, bscale, bzero, yStart); break;
                case 16: Copy2DToMat<short>(fitsData.RawData, stripMat, MatType.CV_16SC1, bscale, bzero, yStart); break;
                case 32: Copy2DToMat<int>(fitsData.RawData, stripMat, MatType.CV_32SC1, bscale, bzero, yStart); break;
                case -32: Copy2DToMat<float>(fitsData.RawData, stripMat, MatType.CV_32FC1, bscale, bzero, yStart); break;
                case -64: Copy2DToMat<double>(fitsData.RawData, stripMat, MatType.CV_64FC1, bscale, bzero, yStart); break;
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

    private void Copy2DToMat<T>(Array sourceArray, Mat destDouble, MatType tempType, double bscale, double bzero, int yStart) 
        where T : struct
    {
        T[,] source2D = (T[,])sourceArray;
        int h = destDouble.Rows;
        int w = destDouble.Cols;

        using Mat tempMat = new Mat(h, w, tempType);
        
        int elementSize = Marshal.SizeOf<T>();

        for (int y = 0; y < h; y++)
        {
            IntPtr destPtr = tempMat.Ptr(y);
            
            // Copia riga per riga in buffer temporaneo per il Marshalling
            T[] rowBuffer = new T[w];
            for (int x = 0; x < w; x++)
            {
                rowBuffer[x] = source2D[y + yStart, x];
            }
            MarshalCopyArrayToPtr(rowBuffer, 0, destPtr, w);
        }

        tempMat.ConvertTo(destDouble, MatType.CV_64FC1, bscale, bzero);
    }

    // =======================================================================
    // 2. MAT (OPENCV) -> RAW (FITS)
    // =======================================================================

    public FitsImageData MatToFitsData(Mat mat, FitsBitDepth targetDepth = FitsBitDepth.Double, FitsHeader? templateHeader = null)
    {
        if (mat == null) throw new ArgumentNullException(nameof(mat));
        if (mat.Empty()) throw new ArgumentException("Matrice vuota", nameof(mat));

        int w = mat.Width;
        int h = mat.Height;
        Array rawData;
        
        MatType requiredType = targetDepth switch
        {
            FitsBitDepth.UInt8  => MatType.CV_8UC1,
            FitsBitDepth.Int16  => MatType.CV_16SC1,
            FitsBitDepth.Int32  => MatType.CV_32SC1,
            FitsBitDepth.Float  => MatType.CV_32FC1,
            _ => MatType.CV_64FC1
        };

        bool needsConversion = mat.Type() != requiredType;
        Mat sourceToRead = mat;
        
        if (needsConversion)
        {
            sourceToRead = new Mat();
            mat.ConvertTo(sourceToRead, requiredType);
        }

        try
        {
            switch (targetDepth)
            {
                case FitsBitDepth.UInt8:  rawData = MatTo2DArray<byte>(sourceToRead, h, w); break;
                case FitsBitDepth.Int16:  rawData = MatTo2DArray<short>(sourceToRead, h, w); break;
                case FitsBitDepth.Int32:  rawData = MatTo2DArray<int>(sourceToRead, h, w); break;
                case FitsBitDepth.Float:  rawData = MatTo2DArray<float>(sourceToRead, h, w); break;
                default:                  rawData = MatTo2DArray<double>(sourceToRead, h, w); break;
            }

            // --- CREAZIONE HEADER ---
            
            var newHeader = new FitsHeader();

            // 1. Chiavi Strutturali (Nuove)
            newHeader.Add("SIMPLE", true, "Standard FITS format");
            newHeader.Add("BITPIX", (int)targetDepth, GetBitpixComment((int)targetDepth));
            newHeader.Add("NAXIS", 2, "2D Image");
            newHeader.Add("NAXIS1", w, "Image Width");
            newHeader.Add("NAXIS2", h, "Image Height");

            // 2. Copia Metadati dal Template (Se presente)
            if (templateHeader != null)
            {
                foreach (var card in templateHeader.Cards)
                {
                    // Saltiamo le chiavi strutturali già definite sopra
                    if (IsStructuralKey(card.Key)) continue; 
                    
                    // --- MODIFICA CRITICA: DEEP COPY ---
                    // Usiamo .Clone() per garantire che il nuovo header sia indipendente dall'originale.
                    newHeader.AddCard(card.Clone());
                }
                
                newHeader.Add("HISTORY", null, "Processed with KomaLab");
            }

            // 3. Scaling Fisico (Resettiamo a 1.0/0.0 per i dati elaborati)
            newHeader.Add("BSCALE", 1.0, "Physical = Raw * BSCALE + BZERO");
            newHeader.Add("BZERO", 0.0, "No Offset");

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

    private bool IsStructuralKey(string key)
    {
        return key == "SIMPLE" || key == "BITPIX" || 
               key == "NAXIS" || key == "NAXIS1" || key == "NAXIS2" || key == "NAXIS3" ||
               key == "BSCALE" || key == "BZERO" || key == "END" || 
               key == "PCOUNT" || key == "GCOUNT" || key == "EXTEND";
    }

    // =======================================================================
    // 3. HELPERS DI MARSHALLING
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