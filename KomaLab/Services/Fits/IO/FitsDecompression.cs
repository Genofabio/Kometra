using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Buffers.Binary;
using KomaLab.Models.Fits.Structure;

namespace KomaLab.Services.Fits.IO;

/// <summary>
/// Servizio di alto livello per la decompressione di immagini FITS compresse (Tile Compression).
/// Coordina la lettura della Binary Table e l'invocazione dei codec specifici (Rice/Gzip).
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

            // 3. LOOP DI ELABORAZIONE TILE (Ogni riga della Binary Table)
            for (int row = 0; row < rowCount; row++)
            {
                // Calcolo coordinate nell'immagine finale
                int tx = (row * zTile1) % naxis1;
                int ty = ((row * zTile1) / naxis1) * zTile2;
                int w = Math.Min(zTile1, naxis1 - tx);
                int h = Math.Min(zTile2, naxis2 - ty);
                int pixelsInTile = w * h;

                long rowStartPos = tableDataStart + ((long)row * rowLen);

                // A. Leggi Descrittore Dati (Size e Offset nello Heap)
                stream.Seek(rowStartPos + offsetData, SeekOrigin.Begin); 
                stream.Read(descriptorBuffer, 0, 8);
                int compressedSize = BinaryPrimitives.ReadInt32BigEndian(descriptorBuffer.AsSpan(0, 4));
                int heapOffset = BinaryPrimitives.ReadInt32BigEndian(descriptorBuffer.AsSpan(4, 4));

                // B. Leggi Scale e Zero (Floating-point quantization)
                double scale = 1.0; double zero = 0.0;
                if (offsetScale >= 0) { stream.Seek(rowStartPos + offsetScale, SeekOrigin.Begin); stream.Read(doubleBuffer, 0, 8); scale = BinaryPrimitives.ReadDoubleBigEndian(doubleBuffer); }
                if (offsetZero >= 0) { stream.Seek(rowStartPos + offsetZero, SeekOrigin.Begin); stream.Read(doubleBuffer, 0, 8); zero = BinaryPrimitives.ReadDoubleBigEndian(doubleBuffer); }

                // C. Estrazione Byte Compessi dallo Heap
                byte[] compressedBytes = new byte[compressedSize];
                if (compressedSize > 0)
                {
                    stream.Seek(heapStart + heapOffset, SeekOrigin.Begin);
                    stream.Read(compressedBytes, 0, compressedSize);
                }

                // D. DECOMPRESSIONE TRAMITE CODEC SPECIFICO
                Array tileData;
                if (cmpType.Contains("RICE"))
                {
                    // Algoritmo Rice-Golomb (RiceCodec)
                    int[] decodedInts = RiceCodec.Decode(compressedBytes, pixelsInTile, 32);
                    tileData = PostProcessDecodedData(decodedInts, zBitpix, scale, zero);
                }
                else if (cmpType.Contains("GZIP") || cmpType.Contains("PLIO"))
                {
                    // Algoritmo Gzip/Deflate (GzipCodec)
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

            // 4. Ripristino stream e salto del blocco dati (incluso eventuale padding MEF)
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

    private static Array PostProcessDecodedData(int[] data, int bitpix, double scale, double zero)
    {
        int count = data.Length;
        // Se l'immagine originale era FLOAT (-32) ma salvata come INT compresso
        if (bitpix == -32)
        {
            float[] f = new float[count];
            for (int i = 0; i < count; i++) f[i] = (float)(data[i] * scale + zero);
            return f;
        }
        // Se era SHORT (16) con offset (es. unsigned 16-bit simulato)
        if (bitpix == 16)
        {
            short[] s = new short[count];
            for (int i = 0; i < count; i++) s[i] = (short)(data[i] * scale + zero);
            return s;
        }
        return data;
    }

    private static Array ConvertAndScale(byte[] bytes, int bitpix, int count, double scale, double zero)
    {
        // Caso Gzip: i byte decompressi sono big-endian raw e vanno convertiti e scalati
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
        
        if (bitpix == 16 && (scale != 1.0 || zero != 0.0))
        {
             short[] result = new short[count];
             int limit = Math.Min(count, bytes.Length / 2);
             for(int i=0; i<limit; i++) {
                 short raw = BinaryPrimitives.ReadInt16BigEndian(bytes.AsSpan(i*2));
                 result[i] = (short)(raw * scale + zero);
             }
             return result;
        }

        return ConvertBytesToPixelArray(bytes, bitpix, count);
    }

    private static Array ConvertBytesToPixelArray(byte[] bytes, int bitpix, int count)
    {
        switch (bitpix) {
            case 8: return bytes;
            case 16: { short[] s = new short[count]; for(int i=0;i<count;i++) s[i]=BinaryPrimitives.ReadInt16BigEndian(bytes.AsSpan(i*2)); return s; }
            case 32: { int[] n = new int[count]; for(int i=0;i<count;i++) n[i]=BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(i*4)); return n; }
            case -32: { float[] f = new float[count]; for(int i=0;i<count;i++) f[i]=BinaryPrimitives.ReadSingleBigEndian(bytes.AsSpan(i*4)); return f; }
            case -64: { double[] d = new double[count]; for(int i=0;i<count;i++) d[i]=BinaryPrimitives.ReadDoubleBigEndian(bytes.AsSpan(i*8)); return d; }
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
        for (int y = 0; y < h; y++) {
            int destY = ty + y; 
            int srcOffset = y * w;
            int destOffset = destY * imgW + tx;
            Buffer.BlockCopy(src, srcOffset * bytesPerPixel, dest, destOffset * bytesPerPixel, w * bytesPerPixel);
        }
    }
}