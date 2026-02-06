using System;
using System.IO;
using System.Text;
using System.Linq;
using System.Collections.Generic;
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
        WriteHeaderInternal(stream, header, isPrimary: true);
    }

    public void WriteImageExtension(Stream stream, FitsHeader header, Array data)
    {
        WriteHeaderInternal(stream, header, isPrimary: false);
        WriteMatrix(stream, data);
    }

    private void WriteHeaderInternal(Stream stream, FitsHeader header, bool isPrimary)
    {
        int bytesWritten = 0;
        var cardsToWrite = new List<FitsCard>(header.Cards);

        cardsToWrite.RemoveAll(c => c.Key == "SIMPLE" || c.Key == "XTENSION" || c.Key == "END");

        if (isPrimary)
        {
            cardsToWrite.Insert(0, new FitsCard("SIMPLE", "T", "Standard FITS Image", false));
        }
        else
        {
            cardsToWrite.Insert(0, new FitsCard("XTENSION", "'IMAGE   '", "Image extension", false));
            EnsureCard(cardsToWrite, "PCOUNT", "0", "No group parameters");
            EnsureCard(cardsToWrite, "GCOUNT", "1", "One group");
        }

        foreach (var card in cardsToWrite)
        {
            string line = FitsFormatting.PadTo80(card);
            byte[] asciiBytes = Encoding.ASCII.GetBytes(line);
            stream.Write(asciiBytes, 0, asciiBytes.Length);
            bytesWritten += 80;
        }

        byte[] endLine = Encoding.ASCII.GetBytes("END".PadRight(80, ' '));
        stream.Write(endLine, 0, endLine.Length);
        bytesWritten += 80;

        WritePadding(stream, bytesWritten, (byte)' ');
    }

    private void EnsureCard(List<FitsCard> cards, string key, string value, string comment)
    {
        if (!cards.Any(c => c.Key == key))
            cards.Insert(1, new FitsCard(key, value, comment, false));
    }

    // =======================================================================
    // 2. SCRITTURA MATRICE (Corretta per il Flip centralizzato)
    // =======================================================================

    public void WriteMatrix(Stream stream, Array matrix)
    {
        Type type = matrix.GetType().GetElementType();
        int h = matrix.GetLength(0);
        int w = matrix.GetLength(1);
        long bytesWritten = 0;

        // NOTA: Il loop ora va da 0 a H-1 perché il Flip è gestito dal FitsIoService
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

        WritePadding(stream, bytesWritten, 0);
    }

    private long WritePixels<T>(Stream s, T[,] data, int w, int h, int bpp, Action<Span<byte>, T> writeFunc)
    {
        byte[] buffer = new byte[w * bpp];
        long count = 0;
        // SCRITTURA SEQUENZIALE: Delega l'ordine Y al chiamante (FitsIoService)
        for (int y = 0; y < h; y++) 
        {
            for (int x = 0; x < w; x++) writeFunc(buffer.AsSpan(x * bpp), data[y, x]);
            s.Write(buffer, 0, buffer.Length);
            count += buffer.Length;
        }
        return count;
    }

    private long WritePixelsByte(Stream s, byte[,] data, int w, int h)
    {
        byte[] buffer = new byte[w];
        long count = 0;
        for (int y = 0; y < h; y++) 
        {
            for (int x = 0; x < w; x++) buffer[x] = data[y, x];
            s.Write(buffer, 0, buffer.Length);
            count += buffer.Length;
        }
        return count;
    }

    private void WritePadding(Stream s, long bytesWrittenSoFar, byte padByte)
    {
        long remainder = bytesWrittenSoFar % BlockSize;
        if (remainder > 0)
        {
            int bytesNeeded = (int)(BlockSize - remainder);
            byte[] buffer = new byte[bytesNeeded];
            if (padByte != 0) Array.Fill(buffer, padByte);
            s.Write(buffer, 0, buffer.Length);
        }
    }
}