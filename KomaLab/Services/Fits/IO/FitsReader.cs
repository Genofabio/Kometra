using System;
using System.IO;
using System.Linq;
using System.Text;
using KomaLab.Models.Fits;
using KomaLab.Services.Fits.Metadata;

namespace KomaLab.Services.Fits.IO;

/// <summary>
/// Motore di basso livello per il parsing binario di file FITS.
/// Gestisce blocchi standard da 2880 byte e conversione Endianness.
/// </summary>
public class FitsReader
{
    private const int BlockSize = 2880;
    private const int CardSize = 80;
    private const int CardsPerBlock = 36;

    // ---------------------------------------------------------------------------
    // 1. LETTURA HEADER
    // ---------------------------------------------------------------------------

    public FitsHeader ReadHeader(Stream stream)
    {
        var header = new FitsHeader();
        byte[] buffer = new byte[BlockSize];
        bool endFound = false;

        while (!endFound)
        {
            int bytesRead = stream.Read(buffer, 0, BlockSize);
            if (bytesRead < BlockSize)
                throw new EndOfStreamException("File FITS troncato o incompleto durante la lettura dell'header.");

            for (int i = 0; i < CardsPerBlock; i++)
            {
                int offset = i * CardSize;
                string line = Encoding.ASCII.GetString(buffer, offset, CardSize);

                // Lo standard FITS termina rigorosamente con "END     "
                if (line.StartsWith("END     "))
                {
                    endFound = true;
                    break;
                }

                var card = FitsFormatting.ParseLine(line);
                
                // Saltiamo le righe completamente vuote (blank cards)
                if (string.IsNullOrWhiteSpace(card.Key) && string.IsNullOrWhiteSpace(card.Value)) 
                    continue;

                header.AddCard(card);
            }
        }

        return header;
    }

    // ---------------------------------------------------------------------------
    // 2. LETTURA MATRICE DATI
    // ---------------------------------------------------------------------------

    public Array ReadMatrix(Stream stream, FitsHeader header)
    {
        // Estrazione interna dei parametri strutturali (per mantenere il Reader autonomo)
        int bitpix = GetInternalInt(header, "BITPIX");
        int width = GetInternalInt(header, "NAXIS1");
        int height = GetInternalInt(header, "NAXIS2");

        if (width <= 0 || height <= 0) 
            return Array.CreateInstance(typeof(byte), 0, 0);

        // Allocazione e lettura in base al BITPIX (FITS Standard)
        Array matrix = bitpix switch
        {
            8 => ReadBytePixels(stream, width, height),
            16 => ReadInt16Pixels(stream, width, height),
            32 => ReadInt32Pixels(stream, width, height),
            -32 => ReadFloatPixels(stream, width, height),
            -64 => ReadDoublePixels(stream, width, height),
            _ => throw new NotSupportedException($"Formato BITPIX {bitpix} non supportato dal motore di lettura.")
        };

        // Allineamento obbligatorio alla fine del blocco da 2880 byte
        SkipPadding(stream, width, height, Math.Abs(bitpix) / 8);

        return matrix;
    }

    // ---------------------------------------------------------------------------
    // 3. METODI DI LETTURA OTTIMIZZATI (Big-Endian -> Little-Endian)
    // ---------------------------------------------------------------------------

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

    private byte[,] ReadBytePixels(Stream s, int w, int h)
    {
        var m = new byte[h, w];
        byte[] rowBuffer = new byte[w];
        for (int y = 0; y < h; y++)
        {
            ReadExact(s, rowBuffer);
            for (int x = 0; x < w; x++) m[y, x] = rowBuffer[x];
        }
        return m;
    }

    // ---------------------------------------------------------------------------
    // 4. HELPERS INTERNI
    // ---------------------------------------------------------------------------

    private int GetInternalInt(FitsHeader header, string key)
    {
        var card = header.Cards.FirstOrDefault(c => c.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (card != null && int.TryParse(card.Value, out int result))
            return result;
        
        // Se mancano BITPIX o NAXIS, il file non è leggibile tecnicamente
        throw new InvalidDataException($"Il file FITS non è valido: chiave strutturale '{key}' mancante.");
    }

    private void ReadExact(Stream s, byte[] b)
    {
        int offset = 0;
        int count = b.Length;
        while (count > 0)
        {
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
            else 
            {
                byte[] junk = new byte[padding];
                ReadExact(stream, junk);
            }
        }
    }
}