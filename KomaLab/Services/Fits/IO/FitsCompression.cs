using System;
using System.Collections.Generic;
using System.IO;
using System.Buffers.Binary;
using KomaLab.Models.Fits.Structure;
using KomaLab.Models.Export; 

namespace KomaLab.Services.Fits.IO;

/// <summary>
/// Servizio per la compressione di immagini FITS secondo lo standard "Tile Compression".
/// Gestisce compressione Lossless (Gzip) e intera (Rice).
/// </summary>
public static class FitsCompression
{
    private const int BlockSize = 32; // Blocchi Rice standard

    public static byte[] CompressImage(Array pixels, FitsHeader originalHeader, FitsCompressionMode mode, out FitsHeader compressedHeader)
    {
        int width = pixels.GetLength(1);
        int height = pixels.GetLength(0);
        int bitpix = GetBitpix(pixels);
        
        // 1. Definiamo il Tiling (Riga per riga)
        int zTile1 = width;
        int zTile2 = 1;
        int rowCount = height;

        // 2. Creiamo l'Header compresso
        compressedHeader = CreateZHeader(originalHeader, bitpix, width, height, zTile1, zTile2, mode);

        // 3. Prepariamo i buffer
        byte[] tableBuffer = new byte[rowCount * 8]; // Descrittori [Size(4)|Offset(4)]
        using var heapStream = new MemoryStream();

        // 4. Ciclo di Compressione Tile
        for (int r = 0; r < rowCount; r++)
        {
            byte[] compressedBlock;

            // --- BIVIO LOGICO: GZIP (Byte-based) vs RICE (Integer-based) ---
            
            if (mode == FitsCompressionMode.Gzip)
            {
                // PERCORSO LOSSLESS: Estrae i byte crudi (Big-Endian) preservando Float/Double
                byte[] rawBytes = GetRawBytesRow(pixels, r, width, bitpix);
                compressedBlock = GzipCodec.Compress(rawBytes);
            }
            else // Rice
            {
                // PERCORSO INTEGER: Converte in interi (Necessario per l'algoritmo Rice)
                // Attenzione: Lossy per Float/Double (arrotondamento)
                int[] rawInts = ExtractRowAsInt32(pixels, r);
                compressedBlock = RiceCodec.Encode(rawInts, BlockSize);
            }

            // --- FINE BIVIO ---

            // C. Aggiornamento Tabella Descrittori
            int compressedSize = compressedBlock.Length;
            int heapOffset = (int)heapStream.Position;

            // Scrittura Big Endian (Standard FITS)
            BinaryPrimitives.WriteInt32BigEndian(tableBuffer.AsSpan(r * 8, 4), compressedSize);
            BinaryPrimitives.WriteInt32BigEndian(tableBuffer.AsSpan(r * 8 + 4, 4), heapOffset);

            // D. Scrittura nello Heap
            heapStream.Write(compressedBlock);
        }

        // 5. Finalizzazione metadati
        compressedHeader.AddOrUpdateCard("PCOUNT", heapStream.Length.ToString(), "Size of the heap");

        // 6. Assemblaggio finale
        byte[] finalHduBody = new byte[tableBuffer.Length + heapStream.Length];
        Buffer.BlockCopy(tableBuffer, 0, finalHduBody, 0, tableBuffer.Length);
        Buffer.BlockCopy(heapStream.GetBuffer(), 0, finalHduBody, tableBuffer.Length, (int)heapStream.Length);

        return finalHduBody;
    }

    /// <summary>
    /// Estrae i byte grezzi in formato Big-Endian. Garantisce la perfetta conservazione dei dati Float/Double.
    /// </summary>
    private static byte[] GetRawBytesRow(Array pixels, int row, int width, int bitpix)
    {
        int bytesPerPixel = Math.Abs(bitpix) / 8;
        byte[] buffer = new byte[width * bytesPerPixel];

        // Utilizziamo switch sul tipo per massimizzare le performance ed evitare boxing
        switch (pixels)
        {
            case byte[,] b: // BITPIX 8
                for (int i = 0; i < width; i++) 
                    buffer[i] = b[row, i];
                break;

            case short[,] s: // BITPIX 16
                for (int i = 0; i < width; i++) 
                    BinaryPrimitives.WriteInt16BigEndian(buffer.AsSpan(i * 2), s[row, i]);
                break;

            case int[,] n: // BITPIX 32
                for (int i = 0; i < width; i++) 
                    BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(i * 4), n[row, i]);
                break;

            case float[,] f: // BITPIX -32 (IEEE-754 Single)
                for (int i = 0; i < width; i++) 
                    BinaryPrimitives.WriteSingleBigEndian(buffer.AsSpan(i * 4), f[row, i]);
                break;

            case double[,] d: // BITPIX -64 (IEEE-754 Double)
                for (int i = 0; i < width; i++) 
                    BinaryPrimitives.WriteDoubleBigEndian(buffer.AsSpan(i * 8), d[row, i]);
                break;
        }

        return buffer;
    }

    /// <summary>
    /// Estrae i dati convertendoli in Int32. Usato ESCLUSIVAMENTE per Rice.
    /// </summary>
    private static int[] ExtractRowAsInt32(Array pixels, int row)
    {
        int w = pixels.GetLength(1);
        int[] result = new int[w];
        
        switch (pixels)
        {
            case short[,] s: for (int i = 0; i < w; i++) result[i] = s[row, i]; break;
            case int[,] i: for (int x = 0; x < w; x++) result[x] = i[row, x]; break;
            case byte[,] b: for (int x = 0; x < w; x++) result[x] = b[row, x]; break;
            // Lossy fallback per Rice su float
            case float[,] f: for (int x = 0; x < w; x++) result[x] = (int)Math.Round(f[row, x]); break;
            case double[,] d: for (int x = 0; x < w; x++) result[x] = (int)Math.Round(d[row, x]); break;
        }
        return result;
    }

    private static FitsHeader CreateZHeader(FitsHeader original, int bitpix, int w, int h, int zt1, int zt2, FitsCompressionMode mode)
    {
        var hC = new FitsHeader();
        
        // Struttura Binary Table
        hC.AddCard(new FitsCard("XTENSION", "'BINTABLE'", "Compressed Image", false));
        hC.AddCard(new FitsCard("BITPIX", "8", "Binary Table data is bytes", false));
        hC.AddCard(new FitsCard("NAXIS", "2", null, false));
        hC.AddCard(new FitsCard("NAXIS1", "8", "Width of table row (descriptor)", false));
        hC.AddCard(new FitsCard("NAXIS2", h.ToString(), "Number of tiles (rows)", false));
        hC.AddCard(new FitsCard("PCOUNT", "0", "Updated at the end", false));
        hC.AddCard(new FitsCard("GCOUNT", "1", null, false));
        hC.AddCard(new FitsCard("TFIELDS", "1", "One column: compressed data", false));
        hC.AddCard(new FitsCard("TFORM1", "'1PB     '", "Variable length array", false));
        hC.AddCard(new FitsCard("TTYPE1", "'COMPRESSED_DATA'", null, false));

        // Z-Keywords
        hC.AddCard(new FitsCard("ZIMAGE", "T", "FITS Tile Compression", false));
        hC.AddCard(new FitsCard("ZCMPTYPE", mode == FitsCompressionMode.Rice ? "'RICE_1  '" : "'GZIP_1  '", "Algorithm", false));
        hC.AddCard(new FitsCard("ZBITPIX", bitpix.ToString(), "Original BITPIX", false));
        hC.AddCard(new FitsCard("ZNAXIS", "2", null, false));
        hC.AddCard(new FitsCard("ZNAXIS1", w.ToString(), null, false));
        hC.AddCard(new FitsCard("ZNAXIS2", h.ToString(), null, false));
        hC.AddCard(new FitsCard("ZTILE1", zt1.ToString(), null, false));
        hC.AddCard(new FitsCard("ZTILE2", zt2.ToString(), null, false));

        // Metadati scientifici
        var structural = new HashSet<string> { "SIMPLE", "BITPIX", "NAXIS", "EXTEND", "END" };
        foreach (var card in original.Cards)
        {
            if (!structural.Contains(card.Key) && !card.Key.StartsWith("NAXIS"))
                hC.AddCard(new FitsCard(card.Key, card.Value, card.Comment, false));
        }

        return hC;
    }

    private static int GetBitpix(Array pixels) => pixels switch
    {
        byte[,] => 8,
        short[,] => 16,
        int[,] => 32,
        float[,] => -32,
        double[,] => -64,
        _ => 16
    };
}