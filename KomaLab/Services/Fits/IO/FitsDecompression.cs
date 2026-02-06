using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Buffers.Binary;
using KomaLab.Models.Fits.Structure;

namespace KomaLab.Services.Fits.IO;

/// <summary>
/// Servizio di alto livello per la decompressione di immagini FITS compresse (Tile Compression).
/// Gestisce sia la decompressione quantizzata (Standard FITS Rice) che Lossless Raw (Gzip Float/Double).
/// </summary>
public static class FitsDecompression
{
    public static Array DecompressImage(Stream stream, FitsHeader header, FitsReader reader)
    {
        try
        {
            Debug.WriteLine("[DEC] --- AVVIO DECOMPRESSIONE TILE-BASED ---");
            long startPos = stream.Position;

            // 1. Parametri Immagine Originale (Z-Keywords)
            int zBitpix = GetInt(header, "ZBITPIX", 16);
            int naxis1 = GetInt(header, "ZNAXIS1", 0); 
            int naxis2 = GetInt(header, "ZNAXIS2", 0); 
            string cmpType = GetString(header, "ZCMPTYPE"); // E.g., RICE_1, GZIP_1
            
            if (naxis1 == 0 || naxis2 == 0) return Array.CreateInstance(typeof(byte), 0, 0);

            // Parametri di Tiling
            int zTile1 = GetInt(header, "ZTILE1", naxis1);
            int zTile2 = GetInt(header, "ZTILE2", 1);

            // Parametri della Binary Table corrente
            int rowCount = GetInt(header, "NAXIS2", 0);    
            int rowLen = GetInt(header, "NAXIS1", 0);      
            long theap = GetLong(header, "THEAP", (long)rowCount * rowLen); 

            // 2. Identificazione Colonne e Offsets
            int colDataIdx = FindColumnIndex(header, "COMPRESSED_DATA") ?? FindColumnIndex(header, "GZIP_COMPRESSED_DATA") ?? 1;
            int? colScaleIdx = FindColumnIndex(header, "ZSCALE");
            int? colZeroIdx = FindColumnIndex(header, "ZZERO");

            long offsetData = (colDataIdx - 1) * 8;
            long offsetScale = colScaleIdx.HasValue ? (colScaleIdx.Value - 1) * 8 : -1;
            long offsetZero = colZeroIdx.HasValue ? (colZeroIdx.Value - 1) * 8 : -1;

            long tableDataStart = stream.Position;
            long heapStart = tableDataStart + theap; 
            
            byte[] descriptorBuffer = new byte[8]; 
            byte[] doubleBuffer = new byte[8];
            Array resultMatrix = AllocateArray(zBitpix, naxis1, naxis2);

            // 3. LOOP DI ELABORAZIONE TILE
            for (int row = 0; row < rowCount; row++)
            {
                // Calcolo coordinate nell'immagine finale
                int tx = (row * zTile1) % naxis1;
                int ty = ((row * zTile1) / naxis1) * zTile2;
                int w = Math.Min(zTile1, naxis1 - tx);
                int h = Math.Min(zTile2, naxis2 - ty);
                int pixelsInTile = w * h;

                long rowStartPos = tableDataStart + ((long)row * rowLen);

                // A. Leggi Descrittore Dati [Size | Offset]
                stream.Seek(rowStartPos + offsetData, SeekOrigin.Begin); 
                stream.Read(descriptorBuffer, 0, 8);
                int compressedSize = BinaryPrimitives.ReadInt32BigEndian(descriptorBuffer.AsSpan(0, 4));
                int heapOffset = BinaryPrimitives.ReadInt32BigEndian(descriptorBuffer.AsSpan(4, 4));

                // B. Leggi Scale e Zero (Se presenti, indicano quantizzazione)
                double scale = 1.0; double zero = 0.0;
                if (offsetScale >= 0) { 
                    stream.Seek(rowStartPos + offsetScale, SeekOrigin.Begin); 
                    stream.Read(doubleBuffer, 0, 8); 
                    scale = BinaryPrimitives.ReadDoubleBigEndian(doubleBuffer); 
                }
                if (offsetZero >= 0) { 
                    stream.Seek(rowStartPos + offsetZero, SeekOrigin.Begin); 
                    stream.Read(doubleBuffer, 0, 8); 
                    zero = BinaryPrimitives.ReadDoubleBigEndian(doubleBuffer); 
                }

                // C. Estrazione Byte Compessi dallo Heap
                byte[] compressedBytes = new byte[compressedSize];
                if (compressedSize > 0)
                {
                    stream.Seek(heapStart + heapOffset, SeekOrigin.Begin);
                    stream.Read(compressedBytes, 0, compressedSize);
                }

                // D. DECOMPRESSIONE
                Array tileData;
                if (cmpType.Contains("RICE"))
                {
                    // Rice restituisce sempre interi, che vanno eventualmente scalati a float
                    int[] decodedInts = RiceCodec.Decode(compressedBytes, pixelsInTile, 32);
                    tileData = PostProcessRiceData(decodedInts, zBitpix, scale, zero);
                }
                else if (cmpType.Contains("GZIP") || cmpType.Contains("PLIO"))
                {
                    // Gzip restituisce byte grezzi: possono essere interi quantizzati O float grezzi (lossless)
                    byte[] rawBytes = GzipCodec.Decompress(compressedBytes);
                    tileData = ConvertAndScale(rawBytes, zBitpix, pixelsInTile, scale, zero);
                }
                else
                {
                    throw new NotSupportedException($"Algoritmo di compressione FITS '{cmpType}' non supportato.");
                }

                // E. Ricostruzione dell'immagine finale
                CopyTileToImage(resultMatrix, tileData, tx, ty, w, h, naxis1, naxis2, zBitpix);
            }

            // 4. Ripristino stream e salto del blocco
            stream.Seek(startPos, SeekOrigin.Begin);
            reader.SkipDataBlock(stream, header);
            
            return resultMatrix;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DEC] ERRORE CRITICO: {ex.Message}");
            throw; 
        }
    }

    // =======================================================================
    // HELPERS DI TRASFORMAZIONE E SCALING
    // =======================================================================

    /// <summary>
    /// Gestisce i dati usciti da Rice (che sono sempre int[]).
    /// </summary>
    private static Array PostProcessRiceData(int[] data, int bitpix, double scale, double zero)
    {
        int count = data.Length;
        // Float quantizzato: Rice ha compresso interi che rappresentano float scalati
        if (bitpix == -32)
        {
            float[] f = new float[count];
            for (int i = 0; i < count; i++) f[i] = (float)(data[i] * scale + zero);
            return f;
        }
        // Double quantizzato (raro per Rice, ma possibile)
        if (bitpix == -64)
        {
            double[] d = new double[count];
            for (int i = 0; i < count; i++) d[i] = (double)(data[i] * scale + zero);
            return d;
        }
        // Short Unsigned simulato
        if (bitpix == 16)
        {
            short[] s = new short[count];
            for (int i = 0; i < count; i++) s[i] = (short)(data[i] * scale + zero);
            return s;
        }
        return data; // Int32 diretti
    }

    /// <summary>
    /// Gestisce i byte usciti da Gzip.
    /// Distingue tra "Byte Grezzi Lossless" e "Interi Quantizzati" basandosi su ZSCALE/ZZERO.
    /// </summary>
    private static Array ConvertAndScale(byte[] bytes, int bitpix, int count, double scale, double zero)
    {
        // Check se è attiva la quantizzazione (scaling diverso da default Identity)
        bool isQuantized = Math.Abs(scale - 1.0) > 1e-9 || Math.Abs(zero) > 1e-9;

        // CASO 1: FLOAT/DOUBLE QUANTIZZATI (Lossy, salvati come Interi)
        // Se c'è scaling su un tipo float, i byte letti sono INT32 BigEndian da convertire.
        if (bitpix < 0 && isQuantized)
        {
            if (bitpix == -32)
            {
                float[] result = new float[count];
                int limit = Math.Min(count, bytes.Length / 4);
                for (int i = 0; i < limit; i++) {
                    int rawInt = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(i * 4));
                    result[i] = (float)(rawInt * scale + zero);
                }
                return result;
            }
            // (Logica analoga per -64 se necessario, ma standard FITS usa int32 per quantizzazione solitamente)
        }
        
        // CASO 2: INTERI SCALATI (Es. Unsigned 16-bit salvati come Signed)
        if (bitpix == 16 && isQuantized)
        {
             short[] result = new short[count];
             int limit = Math.Min(count, bytes.Length / 2);
             for(int i=0; i<limit; i++) {
                 short raw = BinaryPrimitives.ReadInt16BigEndian(bytes.AsSpan(i*2));
                 result[i] = (short)(raw * scale + zero);
             }
             return result;
        }

        // CASO 3: RAW / LOSSLESS (Nessun Scaling o Scaling Identity)
        // Questo è il percorso per il tuo nuovo compressore GZIP:
        // Legge direttamente i byte IEEE-754 float/double o gli interi nativi.
        return ConvertBytesToPixelArray(bytes, bitpix, count);
    }

    private static Array ConvertBytesToPixelArray(byte[] bytes, int bitpix, int count)
    {
        switch (bitpix) {
            case 8: return bytes; // Byte array diretto
            
            case 16: 
            { 
                short[] s = new short[count]; 
                int limit = Math.Min(count, bytes.Length / 2);
                for(int i=0;i<limit;i++) s[i]=BinaryPrimitives.ReadInt16BigEndian(bytes.AsSpan(i*2)); 
                return s; 
            }
            
            case 32: 
            { 
                int[] n = new int[count]; 
                int limit = Math.Min(count, bytes.Length / 4);
                for(int i=0;i<limit;i++) n[i]=BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(i*4)); 
                return n; 
            }
            
            case -32: // Raw Float (IEEE-754 Single Precision)
            { 
                float[] f = new float[count]; 
                int limit = Math.Min(count, bytes.Length / 4);
                for(int i=0;i<limit;i++) f[i]=BinaryPrimitives.ReadSingleBigEndian(bytes.AsSpan(i*4)); 
                return f; 
            }
            
            case -64: // Raw Double (IEEE-754 Double Precision)
            { 
                double[] d = new double[count]; 
                int limit = Math.Min(count, bytes.Length / 8);
                for(int i=0;i<limit;i++) d[i]=BinaryPrimitives.ReadDoubleBigEndian(bytes.AsSpan(i*8)); 
                return d; 
            }
            
            default: return Array.Empty<byte>();
        }
    }

    // =======================================================================
    // METODI DI SUPPORTO E ALLOCAZIONE
    // =======================================================================

    private static int? FindColumnIndex(FitsHeader header, string typeName)
    {
        int tfields = GetInt(header, "TFIELDS", 0);
        for (int i = 1; i <= tfields; i++) {
            if (GetString(header, $"TTYPE{i}") == typeName) return i;
        }
        return null;
    }

    private static int GetInt(FitsHeader h, string k, int def) => int.TryParse(GetString(h, k), out int v) ? v : def;
    private static long GetLong(FitsHeader h, string k, long def) => long.TryParse(GetString(h, k), out long v) ? v : def;
    private static string GetString(FitsHeader h, string k) => h.Cards.FirstOrDefault(c => c.Key == k)?.Value?.Trim().Replace("'", "") ?? "";
    
    private static Array AllocateArray(int bitpix, int w, int h) => bitpix switch { 
        -32 => new float[h, w], 32 => new int[h, w], 16 => new short[h, w], 8 => new byte[h, w], -64 => new double[h,w], _ => new byte[0,0] };

    private static void CopyTileToImage(Array dest, Array src, int tx, int ty, int w, int h, int imgW, int imgH, int bitpix)
    {
        int bytesPerPixel = Math.Abs(bitpix) / 8;
        // Calcolo sicuro per evitare buffer overrun
        int rowBytes = w * bytesPerPixel;

        for (int y = 0; y < h; y++) {
            int destY = ty + y; 
            int srcOffsetBytes = y * rowBytes;
            int destOffsetBytes = (destY * imgW + tx) * bytesPerPixel;
            
            Buffer.BlockCopy(src, srcOffsetBytes, dest, destOffsetBytes, rowBytes);
        }
    }
}