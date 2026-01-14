using System;
using System.IO;
using System.Text;
using KomaLab.Models.Fits;

namespace KomaLab.Services.Fits.Engine;

/// <summary>
/// Componente responsabile della lettura fisica del formato FITS.
/// Gestisce header, dati binari ed endianness.
/// </summary>
public class FitsReader
{
    private const int BlockSize = 2880;
    private const int CardSize = 80;
    private const int CardsPerBlock = 36;

    // --- PARTE 1: HEADER (Già vista, invariata) ---
    
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
                // Se il file è troncato ma abbiamo trovato END, potremmo accettarlo, 
                // ma per ora siamo rigorosi.
                throw new EndOfStreamException("File FITS troncato durante la lettura dell'header.");
            }

            for (int i = 0; i < CardsPerBlock; i++)
            {
                if (endFound) continue;

                int offset = i * CardSize;
                string line = Encoding.ASCII.GetString(buffer, offset, CardSize);

                if (line.StartsWith("END     "))
                {
                    endFound = true;
                    continue; 
                }

                var card = FitsFormatting.ParseLine(line);
                if (string.IsNullOrWhiteSpace(card.Key) && string.IsNullOrWhiteSpace(card.Comment)) continue;

                header.AddCard(card);
            }

            if (endFound) break;
        }

        return header;
    }

    // --- PARTE 2: DATI (Nuova implementazione) ---

    public FitsImageData ReadImage(Stream stream, FitsHeader header)
    {
        // 1. Estrazione Metadati Essenziali
        int bitpix = header.GetIntValue("BITPIX");
        int width = header.GetIntValue("NAXIS1");
        int height = header.GetIntValue("NAXIS2");

        if (width <= 0 || height <= 0) 
            throw new InvalidDataException($"Dimensioni immagine non valide: {width}x{height}");

        // 2. Lettura e Conversione in base al tipo
        Array rawData = bitpix switch
        {
            16 => ReadInt16Pixels(stream, width, height),
            32 => ReadInt32Pixels(stream, width, height),
            -32 => ReadFloatPixels(stream, width, height),
            -64 => ReadDoublePixels(stream, width, height),
            8 => ReadBytePixels(stream, width, height),
            _ => throw new NotSupportedException($"BITPIX {bitpix} non supportato.")
        };

        // 3. Gestione Padding Finale (Dati)
        // I dati sono seguiti da zeri fino a completare il blocco di 2880 byte.
        // Calcoliamo quanti byte abbiamo letto e quanti ne dobbiamo saltare.
        long bytesPerPixel = Math.Abs(bitpix) / 8;
        long totalBytesRead = (long)width * height * bytesPerPixel;
        long remainder = totalBytesRead % BlockSize;
        
        if (remainder > 0)
        {
            long paddingToSkip = BlockSize - remainder;
            if (stream.CanSeek)
            {
                stream.Seek(paddingToSkip, SeekOrigin.Current);
            }
            else
            {
                // Se lo stream non supporta il seek (es. Network), leggiamo a vuoto
                byte[] junk = new byte[Math.Min(paddingToSkip, 4096)];
                while (paddingToSkip > 0)
                {
                    int read = stream.Read(junk, 0, (int)Math.Min(paddingToSkip, junk.Length));
                    if (read == 0) break;
                    paddingToSkip -= read;
                }
            }
        }

        return new FitsImageData
        {
            FitsHeader = header,
            RawData = rawData,
            Width = width,
            Height = height
        };
    }

    // --- Helpers Tipizzati per i Pixel ---

    private short[,] ReadInt16Pixels(Stream stream, int w, int h)
    {
        var result = new short[h, w]; // Matrice 2D [Row, Col]
        int rowBytes = w * 2;
        byte[] buffer = new byte[rowBytes];

        for (int y = 0; y < h; y++)
        {
            ReadExact(stream, buffer);
            // Conversione BigEndian -> LittleEndian per ogni pixel della riga
            for (int x = 0; x < w; x++)
            {
                // Usiamo Span per evitare allocazioni e passare i 2 byte corretti
                result[y, x] = FitsStreamHelper.ReadInt16(buffer.AsSpan(x * 2));
            }
        }
        return result;
    }

    private int[,] ReadInt32Pixels(Stream stream, int w, int h)
    {
        var result = new int[h, w];
        int rowBytes = w * 4;
        byte[] buffer = new byte[rowBytes];

        for (int y = 0; y < h; y++)
        {
            ReadExact(stream, buffer);
            for (int x = 0; x < w; x++)
            {
                result[y, x] = FitsStreamHelper.ReadInt32(buffer.AsSpan(x * 4));
            }
        }
        return result;
    }

    private float[,] ReadFloatPixels(Stream stream, int w, int h)
    {
        var result = new float[h, w];
        int rowBytes = w * 4;
        byte[] buffer = new byte[rowBytes];

        for (int y = 0; y < h; y++)
        {
            ReadExact(stream, buffer);
            for (int x = 0; x < w; x++)
            {
                result[y, x] = FitsStreamHelper.ReadFloat(buffer.AsSpan(x * 4));
            }
        }
        return result;
    }

    private double[,] ReadDoublePixels(Stream stream, int w, int h)
    {
        var result = new double[h, w];
        int rowBytes = w * 8;
        byte[] buffer = new byte[rowBytes];

        for (int y = 0; y < h; y++)
        {
            ReadExact(stream, buffer);
            for (int x = 0; x < w; x++)
            {
                result[y, x] = FitsStreamHelper.ReadDouble(buffer.AsSpan(x * 8));
            }
        }
        return result;
    }
    
    private byte[,] ReadBytePixels(Stream stream, int w, int h)
    {
        // BITPIX 8 è unsigned byte, non serve swap endianness
        var result = new byte[h, w];
        int rowBytes = w;
        byte[] buffer = new byte[rowBytes];

        for (int y = 0; y < h; y++)
        {
            ReadExact(stream, buffer);
            for (int x = 0; x < w; x++)
            {
                result[y, x] = buffer[x];
            }
        }
        return result;
    }

    /// <summary>
    /// Helper per garantire la lettura completa del buffer richiesto.
    /// </summary>
    private void ReadExact(Stream s, byte[] buffer)
    {
        int offset = 0;
        int count = buffer.Length;
        while (count > 0)
        {
            int read = s.Read(buffer, offset, count);
            if (read == 0) throw new EndOfStreamException("Fine del file inattesa durante la lettura dei dati immagine.");
            offset += read;
            count -= read;
        }
    }
}