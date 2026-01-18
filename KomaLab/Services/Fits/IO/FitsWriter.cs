using System;
using System.IO;
using System.Text;
using KomaLab.Models.Fits.Structure;
using KomaLab.Services.Fits.Metadata;

namespace KomaLab.Services.Fits.IO;

public class FitsWriter
{
    private const int BlockSize = 2880;

    // =======================================================================
    // 1. SCRITTURA HEADER
    // =======================================================================

    public void WriteHeader(Stream stream, FitsHeader header)
    {
        int bytesWritten = 0;

        foreach (var card in header.Cards)
        {
            // 1. SALVAGUARDIA: Ignoriamo qualsiasi chiave END presente nella lista.
            // In questo modo, anche se l'utente l'ha spostata a metà, non rompiamo il file.
            if (card.Key.Equals("END", StringComparison.OrdinalIgnoreCase)) 
                continue;

            // Formattiamo la card a 80 caratteri
            string line = FitsFormatting.PadTo80(card);
            
            byte[] asciiBytes = Encoding.ASCII.GetBytes(line);
            stream.Write(asciiBytes, 0, asciiBytes.Length);
            bytesWritten += 80;
        }

        // 2. SCRITTURA FORZATA END
        // La chiave END deve essere fisicamente l'ultima card scritta.
        byte[] endLine = Encoding.ASCII.GetBytes("END".PadRight(80, ' '));
        stream.Write(endLine, 0, endLine.Length);
        bytesWritten += 80;

        // 3. PADDING HEADER: Deve essere fatto con SPAZI (ASCII 32), non zeri!
        WritePadding(stream, bytesWritten, (byte)' ');
    }

    // =======================================================================
    // 2. SCRITTURA MATRICE
    // =======================================================================

    public void WriteMatrix(Stream stream, Array matrix)
    {
        Type type = matrix.GetType().GetElementType();
        int h = matrix.GetLength(0);
        int w = matrix.GetLength(1);
        long bytesWritten = 0;

        if (type == typeof(short)) 
            bytesWritten = WritePixels(stream, (short[,])matrix, w, h, 2, FitsStreamHelper.WriteInt16);
        else if (type == typeof(int)) 
            bytesWritten = WritePixels(stream, (int[,])matrix, w, h, 4, FitsStreamHelper.WriteInt32);
        else if (type == typeof(float)) 
            bytesWritten = WritePixels(stream, (float[,])matrix, w, h, 4, FitsStreamHelper.WriteFloat);
        else if (type == typeof(double)) 
            bytesWritten = WritePixels(stream, (double[,])matrix, w, h, 8, FitsStreamHelper.WriteDouble);
        else if (type == typeof(byte)) 
            bytesWritten = WritePixelsByte(stream, (byte[,])matrix, w, h);
        else
            throw new NotSupportedException($"Tipo {type.Name} non supportato.");

        // 4. PADDING DATI: Deve essere fatto con ZERI (ASCII 0) -> corretto default
        WritePadding(stream, bytesWritten, 0);
    }

    // -----------------------------------------------------------------------
    // HELPERS GENERICI
    // -----------------------------------------------------------------------

    private long WritePixels<T>(Stream s, T[,] data, int w, int h, int bpp, Action<Span<byte>, T> writeFunc)
    {
        byte[] buffer = new byte[w * bpp];
        long count = 0;

        // FITS Standard: Bottom-Up
        for (int y = h - 1; y >= 0; y--)
        {
            for (int x = 0; x < w; x++)
            {
                writeFunc(buffer.AsSpan(x * bpp), data[y, x]);
            }
            s.Write(buffer, 0, buffer.Length);
            count += buffer.Length;
        }
        return count;
    }

    private long WritePixelsByte(Stream s, byte[,] data, int w, int h)
    {
        byte[] buffer = new byte[w];
        long count = 0;

        for (int y = h - 1; y >= 0; y--)
        {
            for (int x = 0; x < w; x++) buffer[x] = data[y, x];
            s.Write(buffer, 0, buffer.Length);
            count += buffer.Length;
        }
        return count;
    }

    // Modificato per accettare il carattere di riempimento (padByte)
    private void WritePadding(Stream s, long bytesWrittenSoFar, byte padByte)
    {
        long remainder = bytesWrittenSoFar % BlockSize;
        if (remainder > 0)
        {
            int bytesNeeded = (int)(BlockSize - remainder);
            byte[] buffer = new byte[bytesNeeded];
            
            // Se il padByte non è zero (es. spazi per l'header), riempiamo l'array
            if (padByte != 0)
            {
                Array.Fill(buffer, padByte);
            }

            s.Write(buffer, 0, buffer.Length);
        }
    }
}