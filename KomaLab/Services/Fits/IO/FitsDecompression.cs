using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Buffers.Binary;
using KomaLab.Models.Fits.Structure;

namespace KomaLab.Services.Fits.IO;

/// <summary>
/// Servizio di decompressione per immagini FITS compresse (Tile Compression).
/// Versione Definitiva: Supporta RICE (con RiceCodec C-style), GZIP e PLIO.
/// </summary>
public static class FitsDecompression
{
    public static Array DecompressImage(Stream stream, FitsHeader header, FitsReader reader)
    {
        try
        {
            // 1. Parametri Immagine Originale
            int zBitpix = GetInt(header, "ZBITPIX", 16);
            int naxis1 = GetInt(header, "ZNAXIS1", 0); 
            int naxis2 = GetInt(header, "ZNAXIS2", 0); 
            string cmpType = GetString(header, "ZCMPTYPE"); 
            
            Debug.WriteLine($"[FitsDecomp] START -> ZBITPIX: {zBitpix}, Size: {naxis1}x{naxis2}, Algo: {cmpType}");

            // Gestione Pixel Nulli
            bool hasZBlank = header.Cards.Any(c => c.Key == "ZBLANK");
            int zBlank = hasZBlank ? GetInt(header, "ZBLANK", 0) : 0;

            if (naxis1 == 0 || naxis2 == 0) return Array.CreateInstance(typeof(byte), 0, 0);

            // Parametri Tiling
            int zTile1 = GetInt(header, "ZTILE1", naxis1);
            int zTile2 = GetInt(header, "ZTILE2", 1);

            // Parametri Struttura Tabella
            int rowCount = GetInt(header, "NAXIS2", 0);    
            int rowLen = GetInt(header, "NAXIS1", 0);      
            long theap = GetLong(header, "THEAP", (long)rowCount * rowLen); 

            // Colonne
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

            // 3. Loop Tile
            for (int row = 0; row < rowCount; row++)
            {
                int tx = (row * zTile1) % naxis1;
                int ty = ((row * zTile1) / naxis1) * zTile2;
                int w = Math.Min(zTile1, naxis1 - tx);
                int h = Math.Min(zTile2, naxis2 - ty);
                int pixelsInTile = w * h;

                long rowStartPos = tableDataStart + ((long)row * rowLen);

                // A. Descrittore
                stream.Seek(rowStartPos + offsetData, SeekOrigin.Begin); 
                stream.Read(descriptorBuffer, 0, 8);
                int compressedSize = BinaryPrimitives.ReadInt32BigEndian(descriptorBuffer.AsSpan(0, 4));
                int heapOffset = BinaryPrimitives.ReadInt32BigEndian(descriptorBuffer.AsSpan(4, 4));

                // B. Scale/Zero
                double scale = 1.0; 
                double zero = 0.0;

                if (offsetScale >= 0) { 
                    stream.Seek(rowStartPos + offsetScale, SeekOrigin.Begin); 
                    stream.Read(doubleBuffer, 0, 8); 
                    scale = BinaryPrimitives.ReadDoubleBigEndian(doubleBuffer); 
                } else {
                    scale = GetDouble(header, "ZBSCALE", 1.0);
                }

                if (offsetZero >= 0) { 
                    stream.Seek(rowStartPos + offsetZero, SeekOrigin.Begin); 
                    stream.Read(doubleBuffer, 0, 8); 
                    zero = BinaryPrimitives.ReadDoubleBigEndian(doubleBuffer); 
                } else {
                    zero = GetDouble(header, "ZBZERO", 0.0);
                }

                // C. Payload
                byte[] compressedBytes = new byte[compressedSize];
                if (compressedSize > 0)
                {
                    stream.Seek(heapStart + heapOffset, SeekOrigin.Begin);
                    stream.Read(compressedBytes, 0, compressedSize);
                }

                // D. Decompressione
                Array tileData;
                if (cmpType.Contains("RICE"))
                {
                    // RICE:
                    // Se ZBITPIX=8 (Byte), fpack usa 16 bit internamente per le differenze.
                    // Altrimenti usa la profondità nativa (16 o 32).
                    int decodeBits = Math.Abs(zBitpix);
                    
                    int[] decodedInts = RiceCodec.Decode(compressedBytes, pixelsInTile, decodeBits);
    
                    // applyPrediction = FALSE perché RiceCodec include già la somma cumulativa.
                    tileData = ProcessDecodedData(decodedInts, zBitpix, scale, zero, hasZBlank, zBlank, false);
                }
                else if (cmpType.Contains("GZIP") || cmpType.Contains("PLIO"))
                {
                    byte[] rawBytes = GzipCodec.Decompress(compressedBytes);
                    tileData = ProcessGzipRawData(rawBytes, zBitpix, pixelsInTile, scale, zero, hasZBlank, zBlank);
                }
                else
                {
                    tileData = Array.CreateInstance(GetPixelType(zBitpix), pixelsInTile);
                }

                // E. Copia
                CopyTileToImage(resultMatrix, tileData, tx, ty, w, h, naxis1, naxis2, zBitpix);
            }

            stream.Seek(tableDataStart, SeekOrigin.Begin);
            reader.SkipDataBlock(stream, header);
            
            return resultMatrix;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FitsDecompression] ERROR: {ex.Message}");
            throw; 
        }
    }

    // =======================================================================
    // LOGICA DI PROCESSO DEI DATI (Scaling, Type Cast)
    // =======================================================================

    private static Array ProcessDecodedData(int[] data, int bitpix, double scale, double zero, bool checkNull, int nullVal, bool applyPrediction)
    {
        int count = data.Length;

        // Predizione opzionale (usata solo se il codec non la fa già)
        if (applyPrediction && bitpix > 0)
        {
            long lastVal = 0;
            for (int i = 0; i < count; i++)
            {
                lastVal += data[i];
                data[i] = (int)lastVal;
            }
        }

        // --- FLOAT / DOUBLE ---
        if (bitpix == -32)
        {
            float[] f = new float[count];
            for (int i = 0; i < count; i++)
            {
                if (checkNull && data[i] == nullVal) f[i] = float.NaN;
                else f[i] = (float)(data[i] * scale + zero);
            }
            return f;
        }
        if (bitpix == -64)
        {
            double[] d = new double[count];
            for (int i = 0; i < count; i++)
            {
                if (checkNull && data[i] == nullVal) d[i] = double.NaN;
                else d[i] = (double)data[i] * scale + zero;
            }
            return d;
        }

        // --- INTERI ---
        bool isUnsigned16 = (bitpix == 16 && Math.Abs(zero - 32768.0) < 1.0);
        bool isUnsigned32 = (bitpix == 32 && Math.Abs(zero - 2147483648.0) < 1.0);

        if (bitpix == 8)
        {
            byte[] b = new byte[count];
            for (int i = 0; i < count; i++) 
            {
                double val = data[i] * scale + zero;
                if (val < 0) val = 0;
                if (val > 255) val = 255;
                b[i] = (byte)val;
            }
            return b;
        }
        else if (bitpix == 16)
        {
            if (isUnsigned16)
            {
                ushort[] us = new ushort[count];
                for (int i = 0; i < count; i++) us[i] = (ushort)((short)data[i] ^ 0x8000);
                return us;
            }
            else
            {
                short[] s = new short[count];
                for (int i = 0; i < count; i++) s[i] = (short)(data[i] * scale + zero);
                return s;
            }
        }
        else if (bitpix == 32)
        {
            if (isUnsigned32)
            {
                uint[] ui = new uint[count];
                for (int i = 0; i < count; i++) ui[i] = (uint)((uint)data[i] ^ 0x80000000);
                return ui;
            }
            else
            {
                if (Math.Abs(scale - 1.0) < 1e-9 && Math.Abs(zero) < 1e-9) return data;
                int[] res = new int[count];
                for (int i = 0; i < count; i++) res[i] = (int)(data[i] * scale + zero);
                return res;
            }
        }
        return data;
    }

    private static Array ProcessGzipRawData(byte[] bytes, int bitpix, int count, double scale, double zero, bool checkNull, int nullVal)
    {
        int[] ints;
        if (bitpix == 16)
        {
            ints = new int[count];
            for (int i = 0; i < count; i++)
            {
                if ((i * 2) + 2 <= bytes.Length)
                    ints[i] = BinaryPrimitives.ReadInt16BigEndian(bytes.AsSpan(i * 2));
            }
        }
        else if (bitpix == 8)
        {
            ints = new int[count];
            for (int i = 0; i < count; i++) if (i < bytes.Length) ints[i] = bytes[i];
        }
        else
        {
            ints = new int[count];
            for (int i = 0; i < count; i++)
            {
                if ((i * 4) + 4 <= bytes.Length)
                    ints[i] = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(i * 4));
            }
        }
        // GZIP solitamente non usa predizione differenziale complessa, quindi false.
        return ProcessDecodedData(ints, bitpix, scale, zero, checkNull, nullVal, false);
    }

    // --- Helpers ---
    private static int? FindColumnIndex(FitsHeader header, string typeName)
    {
        int tfields = GetInt(header, "TFIELDS", 0);
        for (int i = 1; i <= tfields; i++) {
            if (GetString(header, $"TTYPE{i}") == typeName) return i;
        }
        return null;
    }

    private static Type GetPixelType(int bitpix) => bitpix switch { 8 => typeof(byte), 16 => typeof(short), 32 => typeof(int), -32 => typeof(float), -64 => typeof(double), _ => typeof(byte) };
    private static int GetInt(FitsHeader h, string k, int def) => int.TryParse(GetString(h, k), out int v) ? v : def;
    private static long GetLong(FitsHeader h, string k, long def) => long.TryParse(GetString(h, k), out long v) ? v : def;
    private static double GetDouble(FitsHeader h, string k, double def) => double.TryParse(GetString(h, k), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : def;
    private static string GetString(FitsHeader h, string k) => h.Cards.FirstOrDefault(c => c.Key == k)?.Value?.Trim().Replace("'", "") ?? "";
    
    private static Array AllocateArray(int bitpix, int w, int h)
    {
        return bitpix switch { 
            -32 => new float[h, w], -64 => new double[h, w], 32 => new int[h, w], 
            16 => new short[h, w], 8 => new byte[h, w], _ => new byte[0,0] 
        };
    }

    private static void CopyTileToImage(Array dest, Array src, int tx, int ty, int w, int h, int imgW, int imgH, int bitpix)
    {
        int bytesPerElement = Math.Abs(bitpix) / 8;
        int rowBytes = w * bytesPerElement;
        for (int y = 0; y < h; y++) {
            int destY = ty + y;
            if (destY >= imgH) break;
            int srcOffset = y * rowBytes;
            int destOffset = (destY * imgW + tx) * bytesPerElement;
            Buffer.BlockCopy(src, srcOffset, dest, destOffset, rowBytes);
        }
    }
}