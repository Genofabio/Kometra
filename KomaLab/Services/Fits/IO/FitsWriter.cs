using System;
using System.IO;
using System.Text;
using System.Linq;
using System.Collections.Generic;
using System.Buffers.Binary; // Necessario per scrivere BigEndian
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
        
        // 1. Recupera tutte le card tranne quelle strutturali (che riscriviamo in ordine)
        var structuralKeys = new HashSet<string> 
        { 
            "SIMPLE", "XTENSION", "BITPIX", "NAXIS", "NAXIS1", "NAXIS2", "NAXIS3", 
            "PCOUNT", "GCOUNT", "GROUPS", "TFIELDS", "EXTEND", "END" 
        };
        
        var userCards = header.Cards.Where(c => !structuralKeys.Contains(c.Key)).ToList();
        var orderedCards = new List<FitsCard>();

        // 2. Costruisci l'ordine RIGIDO FITS (Standard 4.4.1)
        if (isPrimary)
        {
            orderedCards.Add(new FitsCard("SIMPLE", "T", "Standard FITS Image", false));
            orderedCards.Add(FindOrMake(header, "BITPIX", "8"));
            orderedCards.Add(FindOrMake(header, "NAXIS", "0"));
            AddNaxisCards(header, orderedCards);
            orderedCards.Add(FindOrMake(header, "EXTEND", "T")); // EXTEND solo su Primary
        }
        else
        {
            // Per le estensioni, XTENSION è obbligatorio come prima keyword
            var xtensionCard = header.Cards.FirstOrDefault(c => c.Key == "XTENSION");
            if (xtensionCard.Key == null) 
                xtensionCard = new FitsCard("XTENSION", "'IMAGE   '", "Image extension", false);
                
            orderedCards.Add(xtensionCard);
            orderedCards.Add(FindOrMake(header, "BITPIX", "8"));
            orderedCards.Add(FindOrMake(header, "NAXIS", "0"));
            AddNaxisCards(header, orderedCards);
            orderedCards.Add(FindOrMake(header, "PCOUNT", "0"));
            orderedCards.Add(FindOrMake(header, "GCOUNT", "1"));
            
            // TFIELDS è obbligatorio solo per BINTABLE/TABLE, lo aggiungiamo se esiste
            var tfields = header.Cards.FirstOrDefault(c => c.Key == "TFIELDS");
            if (tfields.Key != null) orderedCards.Add(tfields);
        }

        // 3. Aggiungi tutte le altre card utente
        orderedCards.AddRange(userCards);

        // 4. Scrittura fisica
        foreach (var card in orderedCards)
        {
            string line = FitsFormatting.PadTo80(card);
            byte[] asciiBytes = Encoding.ASCII.GetBytes(line);
            stream.Write(asciiBytes, 0, asciiBytes.Length);
            bytesWritten += 80;
        }

        // 5. Scrittura END e Padding
        byte[] endLine = Encoding.ASCII.GetBytes("END".PadRight(80, ' '));
        stream.Write(endLine, 0, endLine.Length);
        bytesWritten += 80;

        WritePadding(stream, bytesWritten, (byte)' ');
    }

    // --- Helpers per la gestione delle Keyword Strutturali ---

    private FitsCard FindOrMake(FitsHeader source, string key, string defVal)
    {
        var existing = source.Cards.FirstOrDefault(c => c.Key == key);
        // Se la card esiste e ha un valore valido, la usiamo
        if (!string.IsNullOrEmpty(existing.Key)) return existing;
        
        // Altrimenti creiamo un default
        return new FitsCard(key, defVal, null, false);
    }

    private void AddNaxisCards(FitsHeader source, List<FitsCard> target)
    {
        // Trova il valore di NAXIS (già aggiunto o default a 0)
        var naxisCard = target.FirstOrDefault(c => c.Key == "NAXIS");
        if (int.TryParse(naxisCard.Value, out int naxis) && naxis > 0)
        {
            for (int i = 1; i <= naxis; i++)
            {
                target.Add(FindOrMake(source, $"NAXIS{i}", "0"));
            }
        }
    }

    // =======================================================================
    // 2. SCRITTURA MATRICE (Con supporto Unsigned CCD)
    // =======================================================================

    public void WriteMatrix(Stream stream, Array matrix)
    {
        if (matrix == null || matrix.Length == 0) return;

        Type type = matrix.GetType().GetElementType();
        int h = matrix.GetLength(0);
        int w = matrix.GetLength(1);
        long bytesWritten = 0;

        // Gestione di tutti i tipi numerici supportati da FITS (inclusi quelli Unsigned per CCD)
        
        if (type == typeof(byte)) 
            bytesWritten = WritePixelsByte(stream, (byte[,])matrix, w, h);
        
        else if (type == typeof(sbyte)) 
            // Sbyte non è standard FITS diretto, lo trattiamo come byte raw (cast)
            bytesWritten = WritePixelsByte(stream, (sbyte[,])matrix, w, h);

        else if (type == typeof(short)) 
            bytesWritten = WritePixels(stream, (short[,])matrix, w, h, 2, BinaryPrimitives.WriteInt16BigEndian);
        
        else if (type == typeof(ushort))
            // USHORT (16-bit Unsigned): Applicare XOR 0x8000 per centrare su 0 (Standard FITS BZERO=32768)
            bytesWritten = WritePixels(stream, (ushort[,])matrix, w, h, 2, 
                (span, val) => BinaryPrimitives.WriteInt16BigEndian(span, (short)(val ^ 0x8000)));

        else if (type == typeof(int)) 
            bytesWritten = WritePixels(stream, (int[,])matrix, w, h, 4, BinaryPrimitives.WriteInt32BigEndian);
        
        else if (type == typeof(uint))
            // UINT (32-bit Unsigned): Applicare XOR 0x80000000 (Standard FITS BZERO=2147483648)
            bytesWritten = WritePixels(stream, (uint[,])matrix, w, h, 4, 
                (span, val) => BinaryPrimitives.WriteInt32BigEndian(span, (int)(val ^ 0x80000000)));

        else if (type == typeof(float)) 
            bytesWritten = WritePixels(stream, (float[,])matrix, w, h, 4, BinaryPrimitives.WriteSingleBigEndian);
        
        else if (type == typeof(double)) 
            bytesWritten = WritePixels(stream, (double[,])matrix, w, h, 8, BinaryPrimitives.WriteDoubleBigEndian);
        
        else
            throw new NotSupportedException($"Tipo {type.Name} non supportato per la scrittura FITS.");

        WritePadding(stream, bytesWritten, 0);
    }

    // --- Helpers Generici ---

    private long WritePixels<T>(Stream s, T[,] data, int w, int h, int bpp, Action<Span<byte>, T> writeFunc)
    {
        byte[] buffer = new byte[w * bpp];
        long count = 0;
        
        // Loop standard Row-by-Row
        for (int y = 0; y < h; y++) 
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
        for (int y = 0; y < h; y++) 
        {
            for (int x = 0; x < w; x++) buffer[x] = data[y, x];
            s.Write(buffer, 0, buffer.Length);
            count += buffer.Length;
        }
        return count;
    }

    // Overload per sbyte (cast a byte)
    private long WritePixelsByte(Stream s, sbyte[,] data, int w, int h)
    {
        byte[] buffer = new byte[w];
        long count = 0;
        for (int y = 0; y < h; y++) 
        {
            for (int x = 0; x < w; x++) buffer[x] = (byte)data[y, x];
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