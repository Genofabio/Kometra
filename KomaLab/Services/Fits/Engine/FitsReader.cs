using System;
using System.IO;
using System.Text;
using KomaLab.Models.Fits; // Qui dentro c'è FitsHeader e FitsCard

namespace KomaLab.Services.Fits.Engine;

public class FitsReader
{
    private const int BlockSize = 2880;
    private const int CardSize = 80;
    private const int CardsPerBlock = 36;

    /// <summary>
    /// Legge solo l'header.
    /// </summary>
    public FitsHeader ReadHeader(Stream stream)
    {
        var header = new FitsHeader();
        byte[] buffer = new byte[BlockSize];
        bool endFound = false;

        while (true)
        {
            int bytesRead = stream.Read(buffer, 0, BlockSize);
            if (bytesRead < BlockSize)
            {
                if (endFound || (bytesRead == 0 && header.Cards.Count > 0)) break;
                throw new EndOfStreamException("File FITS troncato o incompleto.");
            }

            for (int i = 0; i < CardsPerBlock; i++)
            {
                if (endFound) continue;

                int offset = i * CardSize;
                string line = Encoding.ASCII.GetString(buffer, offset, CardSize);

                if (line.StartsWith("END     ")) endFound = true;

                // Parsing usando la tua classe FitsCard esistente
                var card = FitsFormatting.ParseLine(line);
                
                if (string.IsNullOrWhiteSpace(card.Key) && 
                    string.IsNullOrWhiteSpace(card.Value) && 
                    string.IsNullOrWhiteSpace(card.Comment)) continue;

                header.AddCard(card); // Metodo di FitsHeader che accetta FitsCard
            }

            if (endFound) break;
        }

        return header;
    }

    /// <summary>
    /// Legge la matrice dei pixel.
    /// Restituisce un Array MULTIDIMENSIONALE (es. short[,], float[,]).
    /// </summary>
    public Array ReadMatrix(Stream stream, FitsHeader header)
    {
        int bitpix = header.GetIntValue("BITPIX");
        int width = header.GetIntValue("NAXIS1");
        int height = header.GetIntValue("NAXIS2");

        if (width <= 0 || height <= 0) return new byte[0, 0];

        // Legge la matrice in base al tipo (Switch Expression)
        Array matrix = bitpix switch
        {
            16 => ReadInt16Pixels(stream, width, height),
            32 => ReadInt32Pixels(stream, width, height),
            -32 => ReadFloatPixels(stream, width, height),
            -64 => ReadDoublePixels(stream, width, height),
            8 => ReadBytePixels(stream, width, height),
            _ => throw new NotSupportedException($"BITPIX {bitpix} non supportato.")
        };

        // Gestione Padding (il file FITS è sempre a blocchi di 2880 byte)
        SkipPadding(stream, width, height, Math.Abs(bitpix) / 8);

        return matrix;
    }

    // --- Metodi di Lettura Ottimizzati (Multidimensionali) ---

    private short[,] ReadInt16Pixels(Stream s, int w, int h)
    {
        var m = new short[h, w]; // Matrice [Righe, Colonne]
        byte[] rowBuffer = new byte[w * 2]; // Buffer per una riga intera

        for (int y = 0; y < h; y++)
        {
            ReadExact(s, rowBuffer);
            // Conversione BigEndian -> LittleEndian per ogni pixel
            for (int x = 0; x < w; x++) 
            {
                // Span per performance (evita allocazioni)
                m[y, x] = FitsStreamHelper.ReadInt16(rowBuffer.AsSpan(x * 2));
            }
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
            for (int x = 0; x < w; x++) m[y, x] = FitsStreamHelper.ReadInt32(rowBuffer.AsSpan(x * 4));
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
            for (int x = 0; x < w; x++) m[y, x] = FitsStreamHelper.ReadFloat(rowBuffer.AsSpan(x * 4));
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
            for (int x = 0; x < w; x++) m[y, x] = FitsStreamHelper.ReadDouble(rowBuffer.AsSpan(x * 8));
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

    // --- Helpers ---

    private void ReadExact(Stream s, byte[] b)
    {
        int offset = 0;
        int count = b.Length;
        while (count > 0)
        {
            int read = s.Read(b, offset, count);
            if (read == 0) throw new EndOfStreamException();
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
                byte[] junk = new byte[Math.Min(padding, 4096)];
                while (padding > 0)
                {
                    int r = stream.Read(junk, 0, (int)Math.Min(padding, junk.Length));
                    if (r == 0) break;
                    padding -= r;
                }
            }
        }
    }
}