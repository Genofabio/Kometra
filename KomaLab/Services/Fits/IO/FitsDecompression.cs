using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Diagnostics;
using System.Buffers.Binary;
using System.Threading.Tasks;
using KomaLab.Models.Fits.Structure;

namespace KomaLab.Services.Fits.IO;

public static class FitsDecompression
{
    public static Array DecompressImage(Stream stream, FitsHeader header, FitsReader reader)
    {
        try
        {
            Debug.WriteLine("[DEC] --- INIZIO DECOMPRESSIONE (OFFSET FIX) ---");
            long startPos = stream.Position;

            // 1. Parametri Header
            int zBitpix = GetInt(header, "ZBITPIX", 16);
            int naxis1 = GetInt(header, "ZNAXIS1", 0); 
            int naxis2 = GetInt(header, "ZNAXIS2", 0); 
            string cmpType = GetString(header, "ZCMPTYPE");
            
            if (naxis1 == 0 || naxis2 == 0) return Array.CreateInstance(typeof(byte), 0, 0);

            // Parametri Tile
            int zTile1 = GetInt(header, "ZTILE1", naxis1);
            int zTile2 = GetInt(header, "ZTILE2", 1);

            // Parametri Tabella
            int rowCount = GetInt(header, "NAXIS2", 0);    
            int rowLen = GetInt(header, "NAXIS1", 0);      
            long theap = GetLong(header, "THEAP", (long)rowCount * rowLen); 

            // 2. IDENTIFICAZIONE COLONNE E OFFSET
            // In FITS Binary Table, ogni "cella" ha una larghezza fissa.
            // Le colonne '1PB' (Data), '1D' (Scale), '1D' (Zero) sono tutte larghe 8 byte.
            // Calcoliamo l'offset esatto: (IndiceColonna - 1) * 8.
            
            int colDataIdx = FindColumnIndex(header, "COMPRESSED_DATA") ?? FindColumnIndex(header, "GZIP_COMPRESSED_DATA") ?? 1;
            int? colScaleIdx = FindColumnIndex(header, "ZSCALE");
            int? colZeroIdx = FindColumnIndex(header, "ZZERO");

            // Se la colonna Dati è la #2, l'offset è (2-1)*8 = 8 byte.
            // Se RowLen è 32 e abbiamo col 2,3,4 -> 8+8+8=24 byte. Avanzano 8 byte (Col 1). Tutto torna.
            long offsetData = (colDataIdx - 1) * 8;
            long offsetScale = colScaleIdx.HasValue ? (colScaleIdx.Value - 1) * 8 : -1;
            long offsetZero = colZeroIdx.HasValue ? (colZeroIdx.Value - 1) * 8 : -1;

            Debug.WriteLine($"[DEC] Layout: RowLen={rowLen} | DataCols=#{colDataIdx} (@{offsetData}) | Scale=#{colScaleIdx} (@{offsetScale}) | Zero=#{colZeroIdx} (@{offsetZero})");

            // Setup Stream
            long tableDataStart = stream.Position;
            long heapStart = tableDataStart + theap; 
            byte[] descriptorBuffer = new byte[8]; 
            byte[] doubleBuffer = new byte[8];
            Array resultMatrix = AllocateArray(zBitpix, naxis1, naxis2);

            // 3. LOOP TILE
            for (int row = 0; row < rowCount; row++)
            {
                int tx = (row * zTile1) % naxis1;
                int ty = ((row * zTile1) / naxis1) * zTile2;
                int w = Math.Min(zTile1, naxis1 - tx);
                int h = Math.Min(zTile2, naxis2 - ty);
                int pixelsInTile = w * h;

                long rowStartPos = tableDataStart + ((long)row * rowLen);

                // A. Leggi Descrittore Dati (Posizione Dinamica)
                stream.Seek(rowStartPos + offsetData, SeekOrigin.Begin); 
                stream.Read(descriptorBuffer, 0, 8);
                int compressedSize = BinaryPrimitives.ReadInt32BigEndian(descriptorBuffer.AsSpan(0, 4));
                int heapOffset = BinaryPrimitives.ReadInt32BigEndian(descriptorBuffer.AsSpan(4, 4));

                // B. Leggi Scale e Zero (Se presenti)
                double scale = 1.0;
                double zero = 0.0; // Default per interi

                // Se ZBITPIX è Float, il default FITS per ZZERO è 0.0? 
                // Attenzione: se ZSCALE/ZZERO mancano, i dati sono raw.
                // Se mancano ma siamo in mode Float-as-Int, di solito ZSCALE=1, ZZERO=0.
                
                if (offsetScale >= 0)
                {
                    stream.Seek(rowStartPos + offsetScale, SeekOrigin.Begin);
                    stream.Read(doubleBuffer, 0, 8);
                    scale = BinaryPrimitives.ReadDoubleBigEndian(doubleBuffer);
                }
                
                if (offsetZero >= 0)
                {
                    stream.Seek(rowStartPos + offsetZero, SeekOrigin.Begin);
                    stream.Read(doubleBuffer, 0, 8);
                    zero = BinaryPrimitives.ReadDoubleBigEndian(doubleBuffer);
                }

                if (row == 0) Debug.WriteLine($"[DEC] TILE 0: Size={compressedSize}, Scale={scale}, Zero={zero}");
                // Se Scale è NaN o numeri assurdi (es. 1E+300), l'offset è ancora sbagliato.
                if (double.IsNaN(scale)) scale = 1.0; 

                // C. Leggi Payload
                byte[] compressedBytes = new byte[compressedSize];
                if (compressedSize > 0)
                {
                    stream.Seek(heapStart + heapOffset, SeekOrigin.Begin);
                    stream.Read(compressedBytes, 0, compressedSize);
                }

                // D. Decompressione
                byte[] rawBytes = DecompressGzipBytes(compressedBytes);

                // E. Conversione e Scaling
                Array tileData = ConvertAndScale(rawBytes, zBitpix, pixelsInTile, scale, zero);

                // F. Copia
                CopyTileToImage(resultMatrix, tileData, tx, ty, w, h, naxis1, naxis2, zBitpix);
            }

            stream.Seek(startPos, SeekOrigin.Begin);
            reader.SkipDataBlock(stream, header);
            
            DebugAnalyzeData(resultMatrix);
            return resultMatrix;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DEC] CRASH: {ex}");
            throw; 
        }
    }

    private static Array ConvertAndScale(byte[] bytes, int bitpix, int count, double scale, double zero)
    {
        // Se ZBITPIX = -32 (Float), i dati sono salvati come INT32 e scalati.
        if (bitpix == -32)
        {
            float[] result = new float[count];
            // Safe guard: se bytes non bastano
            int limit = Math.Min(count, bytes.Length / 4);
            
            for (int i = 0; i < limit; i++)
            {
                int rawInt = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(i * 4));
                result[i] = (float)(rawInt * scale + zero);
            }
            return result;
        }
        
        // Se ZBITPIX = 16 (Short), spesso usano ZZERO=32768 per simulare unsigned short
        if (bitpix == 16 && (scale != 1.0 || zero != 0.0))
        {
             short[] result = new short[count];
             int limit = Math.Min(count, bytes.Length / 2);
             for(int i=0; i<limit; i++)
             {
                 short raw = BinaryPrimitives.ReadInt16BigEndian(bytes.AsSpan(i*2));
                 result[i] = (short)(raw * scale + zero);
             }
             return result;
        }

        return ConvertBytesToPixelArray(bytes, bitpix, count);
    }

    private static byte[] DecompressGzipBytes(byte[] input)
    {
        if (input.Length == 0) return new byte[0];
        try {
            using var msInput = new MemoryStream(input);
            using var gzip = new GZipStream(msInput, CompressionMode.Decompress);
            using var msOutput = new MemoryStream();
            gzip.CopyTo(msOutput);
            return msOutput.ToArray();
        } catch {
            using var msInput = new MemoryStream(input);
            using var deflate = new DeflateStream(msInput, CompressionMode.Decompress);
            using var msOutput = new MemoryStream();
            deflate.CopyTo(msOutput);
            return msOutput.ToArray();
        }
    }

    private static Array ConvertBytesToPixelArray(byte[] bytes, int bitpix, int count)
    {
        switch (bitpix) {
            case 8: return bytes;
            case 16: { short[] s = new short[count]; for(int i=0;i<count;i++) s[i]=BinaryPrimitives.ReadInt16BigEndian(bytes.AsSpan(i*2)); return s; }
            case 32: { int[] n = new int[count]; for(int i=0;i<count;i++) n[i]=BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(i*4)); return n; }
            case -32: { float[] f = new float[count]; for(int i=0;i<count;i++) f[i]=BinaryPrimitives.ReadSingleBigEndian(bytes.AsSpan(i*4)); return f; }
            case -64: { double[] d = new double[count]; for(int i=0;i<count;i++) d[i]=BinaryPrimitives.ReadDoubleBigEndian(bytes.AsSpan(i*8)); return d; }
            default: return new byte[0];
        }
    }

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
        for (int y = 0; y < h; y++) {
            int destY = ty + y; 
            int srcOffset = y * w;
            int destOffset = destY * imgW + tx;
            int bytesPerPixel = Math.Abs(bitpix) / 8;
            int bytesToCopy = w * bytesPerPixel;
            Buffer.BlockCopy(src, srcOffset * bytesPerPixel, dest, destOffset * bytesPerPixel, bytesToCopy);
        }
    }
    
    private static void DebugAnalyzeData(Array matrix) {
        double min = double.MaxValue, max = double.MinValue;
        if(matrix is float[,] f && f.Length > 0) { 
            Debug.WriteLine($"[DEC CHECK] First Pixel: {f[0,0]}");
            foreach(var v in f) { if(v<min)min=v; if(v>max)max=v; }
            Debug.WriteLine($"[DEC CHECK] Stats: Min={min} Max={max}");
        }
    }
}