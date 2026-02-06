using System;
using System.Collections.Generic;
using System.IO;
using System.Buffers.Binary;
using KomaLab.Models.Fits.Structure;
using KomaLab.Models.Export; // Per l'enum FitsCompressionMode

namespace KomaLab.Services.Fits.IO;

/// <summary>
/// Servizio per la compressione di immagini FITS secondo lo standard "Tile Compression".
/// Converte una matrice di pixel in una Binary Table compressa (MEF compatibile).
/// </summary>
public static class FitsCompression
{
    private const int BlockSize = 32; // Blocchi Rice standard

    public static byte[] CompressImage(Array pixels, FitsHeader originalHeader, FitsCompressionMode mode, out FitsHeader compressedHeader)
    {
        int width = pixels.GetLength(1);
        int height = pixels.GetLength(0);
        int bitpix = GetBitpix(pixels);
        
        // 1. Definiamo il Tiling (Riga per riga è lo standard più compatibile)
        int zTile1 = width;
        int zTile2 = 1;
        int rowCount = height;

        // 2. Creiamo l'Header compresso con le Z-Keywords
        compressedHeader = CreateZHeader(originalHeader, bitpix, width, height, zTile1, zTile2, mode);

        // 3. Prepariamo i buffer per la Binary Table e lo Heap
        // Ogni riga della tabella è un descrittore di 8 byte: [Size | Offset]
        byte[] tableBuffer = new byte[rowCount * 8];
        using var heapStream = new MemoryStream();

        // 4. Ciclo di Compressione Tile
        for (int r = 0; r < rowCount; r++)
        {
            // A. Estrazione dati della riga e conversione in Int32 (Quantizzazione)
            // Nota: Rice lavora solo su interi.
            int[] rawInts = ExtractRowAsInt32(pixels, r);

            // B. Compressione tramite Codec
            byte[] compressedBlock;
            if (mode == FitsCompressionMode.Rice)
            {
                compressedBlock = RiceCodec.Encode(rawInts, BlockSize);
            }
            else
            {
                // Per Gzip, dobbiamo convertire gli Int32 in Byte Raw (Big Endian) prima di comprimere
                byte[] rawBytes = PrepareBytesForGzip(rawInts);
                compressedBlock = GzipCodec.Compress(rawBytes);
            }

            // C. Aggiornamento Tabella Descrittori
            int compressedSize = compressedBlock.Length;
            int heapOffset = (int)heapStream.Position;

            // Scrittura Big Endian (Standard FITS)
            BinaryPrimitives.WriteInt32BigEndian(tableBuffer.AsSpan(r * 8, 4), compressedSize);
            BinaryPrimitives.WriteInt32BigEndian(tableBuffer.AsSpan(r * 8 + 4, 4), heapOffset);

            // D. Scrittura nello Heap
            heapStream.Write(compressedBlock);
        }

        // 5. Finalizzazione metadati (PCOUNT indica la dimensione dello Heap)
        compressedHeader.AddOrUpdateCard("PCOUNT", heapStream.Length.ToString(), "Size of the heap");

        // 6. Assemblaggio finale: Tabella + Heap
        byte[] finalHduBody = new byte[tableBuffer.Length + heapStream.Length];
        Buffer.BlockCopy(tableBuffer, 0, finalHduBody, 0, tableBuffer.Length);
        Buffer.BlockCopy(heapStream.GetBuffer(), 0, finalHduBody, tableBuffer.Length, (int)heapStream.Length);

        return finalHduBody;
    }

    private static FitsHeader CreateZHeader(FitsHeader original, int bitpix, int w, int h, int zt1, int zt2, FitsCompressionMode mode)
    {
        var hC = new FitsHeader();
        
        // Keywords Strutturali della Binary Table
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

        // Z-Keywords (Metadati dell'immagine originale)
        hC.AddCard(new FitsCard("ZIMAGE", "T", "FITS Tile Compression", false));
        hC.AddCard(new FitsCard("ZCMPTYPE", mode == FitsCompressionMode.Rice ? "'RICE_1  '" : "'GZIP_1  '", "Algorithm", false));
        hC.AddCard(new FitsCard("ZBITPIX", bitpix.ToString(), "Original BITPIX", false));
        hC.AddCard(new FitsCard("ZNAXIS", "2", null, false));
        hC.AddCard(new FitsCard("ZNAXIS1", w.ToString(), null, false));
        hC.AddCard(new FitsCard("ZNAXIS2", h.ToString(), null, false));
        hC.AddCard(new FitsCard("ZTILE1", zt1.ToString(), null, false));
        hC.AddCard(new FitsCard("ZTILE2", zt2.ToString(), null, false));

        // Copiamo i metadati scientifici dall'originale (escludendo quelli strutturali)
        var structural = new HashSet<string> { "SIMPLE", "BITPIX", "NAXIS", "EXTEND", "END" };
        foreach (var card in original.Cards)
        {
            if (!structural.Contains(card.Key) && !card.Key.StartsWith("NAXIS"))
                hC.AddCard(new FitsCard(card.Key, card.Value, card.Comment, false));
        }

        return hC;
    }

    private static int[] ExtractRowAsInt32(Array pixels, int row)
    {
        int w = pixels.GetLength(1);
        int[] result = new int[w];
        
        // Conversione/Quantizzazione (Semplificata: assumiamo interi per ora)
        // Se in futuro userai Float, qui dovrai applicare ZSCALE/ZZERO
        switch (pixels)
        {
            case short[,] s: for (int i = 0; i < w; i++) result[i] = s[row, i]; break;
            case int[,] i: for (int x = 0; x < w; x++) result[x] = i[row, x]; break;
            case byte[,] b: for (int x = 0; x < w; x++) result[x] = b[row, x]; break;
            case float[,] f: for (int x = 0; x < w; x++) result[x] = (int)Math.Round(f[row, x]); break;
        }
        return result;
    }

    private static byte[] PrepareBytesForGzip(int[] data)
    {
        byte[] buffer = new byte[data.Length * 4];
        for (int i = 0; i < data.Length; i++)
            BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(i * 4, 4), data[i]);
        return buffer;
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