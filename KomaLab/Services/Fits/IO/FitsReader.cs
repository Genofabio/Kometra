using System;
using System.IO;
using System.Linq;
using System.Text;
using KomaLab.Models.Fits;
using KomaLab.Models.Fits.Structure;
using KomaLab.Services.Fits.Metadata;

namespace KomaLab.Services.Fits.IO;

public class FitsReader
{
    private const int BlockSize = 2880;
    private const int CardSize = 80;
    private const int CardsPerBlock = 36;

    public FitsHeader ReadHeader(Stream stream)
    {
        var header = new FitsHeader();
        byte[] buffer = new byte[BlockSize];
        bool endFound = false;

        while (!endFound)
        {
            if (stream.Position >= stream.Length) 
                throw new EndOfStreamException("Fine del file inaspettata prima di trovare END.");

            int bytesRead = stream.Read(buffer, 0, BlockSize);
            if (bytesRead < BlockSize)
                throw new EndOfStreamException("File FITS troncato o incompleto durante la lettura dell'header.");

            for (int i = 0; i < CardsPerBlock; i++)
            {
                int offset = i * CardSize;
                string line = Encoding.ASCII.GetString(buffer, offset, CardSize);

                if (line.StartsWith("END     "))
                {
                    endFound = true;
                    header.AddCard(new FitsCard("END", string.Empty, string.Empty, true));
                    break; 
                }

                var card = FitsFormatting.ParseLine(line);
                if (string.IsNullOrWhiteSpace(card.Key) && string.IsNullOrWhiteSpace(card.Value)) 
                    continue;

                header.AddCard(card);
            }
        }

        return header;
    }

    public Array ReadMatrix(Stream stream, FitsHeader header)
    {
        int bitpix = GetInternalInt(header, "BITPIX", 0);
        int width = GetInternalInt(header, "NAXIS1", 0);
        int height = GetInternalInt(header, "NAXIS2", 0);

        // [MODIFICA FULL MEF] 
        // Se l'HDU è vuoto (es. Primary HDU di un MEF), calcoliamo se c'è
        // del padding o dati extra (PCOUNT) da saltare e restituiamo un array vuoto.
        // NON lanciamo eccezioni, perché è un HDU valido (solo metadata).
        if (width <= 0 || height <= 0) 
        {
            SkipDataBlock(stream, header); 
            // Restituisce un array 0x0
            return Array.CreateInstance(typeof(byte), 0, 0);
        }

        Array matrix = bitpix switch
        {
            8 => ReadBytePixels(stream, width, height),
            16 => ReadInt16Pixels(stream, width, height),
            32 => ReadInt32Pixels(stream, width, height),
            -32 => ReadFloatPixels(stream, width, height),
            -64 => ReadDoublePixels(stream, width, height),
            _ => throw new NotSupportedException($"Formato BITPIX {bitpix} non supportato.")
        };

        SkipPadding(stream, width, height, Math.Abs(bitpix) / 8);
        return matrix;
    }

    /// <summary>
    /// Calcola la dimensione del blocco dati corrente e sposta lo stream avanti.
    /// </summary>
    public void SkipDataBlock(Stream stream, FitsHeader header)
    {
        long dataSize = CalculateDataSizeInBytes(header);
        
        if (dataSize > 0)
        {
            long remainder = dataSize % BlockSize;
            long padding = (remainder == 0) ? 0 : (BlockSize - remainder);
            long totalSkip = dataSize + padding;

            if (stream.CanSeek) stream.Seek(totalSkip, SeekOrigin.Current);
            else
            {
                byte[] junk = new byte[Math.Min(totalSkip, 4096)];
                long toSkip = totalSkip;
                while (toSkip > 0)
                {
                    int read = stream.Read(junk, 0, (int)Math.Min(toSkip, junk.Length));
                    if (read == 0) break;
                    toSkip -= read;
                }
            }
        }
    }

    private long CalculateDataSizeInBytes(FitsHeader header)
    {
        int bitpix = Math.Abs(GetInternalInt(header, "BITPIX", 0));
        int naxis = GetInternalInt(header, "NAXIS", 0);
        long pcount = GetInternalLong(header, "PCOUNT", 0);
        long gcount = GetInternalLong(header, "GCOUNT", 1);

        if (naxis == 0 && pcount == 0) return 0;

        long numPixels = 1;
        bool hasAxes = false;

        for (int i = 1; i <= naxis; i++)
        {
            long axisLen = GetInternalLong(header, $"NAXIS{i}", 0);
            numPixels *= axisLen;
            hasAxes = true;
        }

        if (!hasAxes) numPixels = 0;

        // Formula FITS standard: (N_PIXELS + PCOUNT) * GCOUNT * (BITPIX / 8)
        long totalElements = (numPixels + pcount) * gcount;
        long totalBits = totalElements * bitpix;
        
        return totalBits / 8;
    }

    // --- Metodi Pixel (ReadBytePixels, ReadInt16Pixels, ecc.) rimangono invariati ---
    private byte[,] ReadBytePixels(Stream s, int w, int h)
    {
        var m = new byte[h, w];
        byte[] rowBuffer = new byte[w];
        for (int y = 0; y < h; y++) { ReadExact(s, rowBuffer); for (int x = 0; x < w; x++) m[y, x] = rowBuffer[x]; }
        return m;
    }
    // ... Inserire qui gli altri metodi (ReadInt16, ReadFloat, etc.) come nel codice precedente ...
    
    // --- Helpers (ReadExact, SkipPadding, GetInternal...) rimangono invariati ---
    private int GetInternalInt(FitsHeader header, string key, int defVal)
    {
        var card = header.Cards.FirstOrDefault(c => c.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (card != null && int.TryParse(card.Value, out int result)) return result;
        return defVal;
    }
    private long GetInternalLong(FitsHeader header, string key, long defVal)
    {
        var card = header.Cards.FirstOrDefault(c => c.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (card != null && long.TryParse(card.Value, out long result)) return result;
        return defVal;
    }
    private void ReadExact(Stream s, byte[] b)
    {
        int offset = 0;
        int count = b.Length;
        while (count > 0) {
            int read = s.Read(b, offset, count);
            if (read == 0) throw new EndOfStreamException("Fine del file inaspettata durante la lettura dei pixel.");
            offset += read;
            count -= read;
        }
    }
    private void SkipPadding(Stream stream, int w, int h, int bytesPerPixel)
    {
        long totalBytes = (long)w * h * bytesPerPixel;
        long remainder = totalBytes % BlockSize;
        if (remainder > 0)
        {
            long padding = BlockSize - remainder;
            if (stream.CanSeek) stream.Seek(padding, SeekOrigin.Current);
            else { byte[] junk = new byte[padding]; ReadExact(stream, junk); }
        }
    }
    private short[,] ReadInt16Pixels(Stream s, int w, int h)
    {
        var m = new short[h, w];
        byte[] rowBuffer = new byte[w * 2];
        for (int y = 0; y < h; y++)
        {
            ReadExact(s, rowBuffer);
            for (int x = 0; x < w; x++) 
                m[y, x] = FitsStreamHelper.ReadInt16(rowBuffer.AsSpan(x * 2));
        }
        return m;
    }

    private int[,] ReadInt32Pixels(Stream s, int w, int h)
    {
        var m = new int[h, w];
        byte[] rowBuffer = new byte[w * 4];
        for (int y = 0; y < h; y++)
        {
            ReadExact(s, rowBuffer);
            for (int x = 0; x < w; x++) 
                m[y, x] = FitsStreamHelper.ReadInt32(rowBuffer.AsSpan(x * 4));
        }
        return m;
    }

    private float[,] ReadFloatPixels(Stream s, int w, int h)
    {
        var m = new float[h, w];
        byte[] rowBuffer = new byte[w * 4];
        for (int y = 0; y < h; y++)
        {
            ReadExact(s, rowBuffer);
            for (int x = 0; x < w; x++) 
                m[y, x] = FitsStreamHelper.ReadFloat(rowBuffer.AsSpan(x * 4));
        }
        return m;
    }

    private double[,] ReadDoublePixels(Stream s, int w, int h)
    {
        var m = new double[h, w];
        byte[] rowBuffer = new byte[w * 8];
        for (int y = 0; y < h; y++)
        {
            ReadExact(s, rowBuffer);
            for (int x = 0; x < w; x++) 
                m[y, x] = FitsStreamHelper.ReadDouble(rowBuffer.AsSpan(x * 8));
        }
        return m;
    }
}