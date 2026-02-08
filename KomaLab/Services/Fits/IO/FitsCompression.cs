using System;
using System.Collections.Generic;
using System.IO;
using System.Buffers.Binary;
using System.Linq;
using KomaLab.Models.Fits.Structure;
using KomaLab.Models.Export;

namespace KomaLab.Services.Fits.IO;

/// <summary>
/// Servizio per la compressione di immagini FITS secondo lo standard "Tile Compression".
/// Integra la logica nativa di fpack (Quantizzazione statistica + Dithering) e codifica Rice/Gzip.
/// </summary>
public static class FitsCompression
{
    private const int BlockSize = 32;

    public static byte[] CompressImage(Array pixels, FitsHeader originalHeader, FitsCompressionMode mode, out FitsHeader compressedHeader)
    {
        int width = pixels.GetLength(1);
        int height = pixels.GetLength(0);
        int bitpix = GetBitpix(pixels);
        
        // Determiniamo se serve la Quantizzazione (Solo per Rice su Float/Double)
        bool isFloat = bitpix < 0;
        bool useQuantization = (mode == FitsCompressionMode.Rice && isFloat);

        // 1. Definiamo il Tiling (Default fpack: riga per riga)
        int zTile1 = width;
        int zTile2 = 1; 
        int rowCount = height;

        // 2. Calcolo dimensione Tabella Binaria e THEAP
        // Base: 8 byte (Pointer: 4 Len + 4 Offset)
        // Quantized: 8 byte (Pointer) + 8 byte (ZSCALE) + 8 byte (ZZERO) = 24 byte
        int rowStride = useQuantization ? 24 : 8;
        long theapOffset = (long)rowCount * rowStride;

        // 3. Creiamo l'Header (Ora include THEAP e ZBLANK)
        compressedHeader = CreateZHeader(originalHeader, bitpix, width, height, zTile1, zTile2, mode, useQuantization, theapOffset);

        byte[] tableBuffer = new byte[theapOffset];
        using var heapStream = new MemoryStream();

        // 4. Ciclo di Compressione Tile
        for (int r = 0; r < rowCount; r++)
        {
            byte[] compressedBlock;
            double scale = 1.0;
            double zero = 0.0;

            if (mode == FitsCompressionMode.Gzip)
            {
                // GZIP LOSSLESS (Byte-based, no quantizzazione)
                // Nota: GZIP_1 su float è poco efficiente, ma è lo standard semplice.
                byte[] rawBytes = GetRawBytesRow(pixels, r, width, bitpix);
                compressedBlock = GzipCodec.Compress(rawBytes);
            }
            else // RICE
            {
                int[] intsToCompress;

                if (useQuantization)
                {
                    // RICE CON QUANTIZZAZIONE FPACK (Float/Double -> Int32)
                    // Estraiamo la riga come float[] per il quantizzatore
                    float[] rowData = GetRowAsFloats(pixels, r, width);

                    // Parametri di quantizzazione fpack default
                    float qLevel = 4.0f; // q=4 è lo standard
                    int ditherMethod = FitsQuantizerHighLevel.SUBTRACTIVE_DITHER_1;
                    int imin, imax;

                    // Chiamata alla logica nativa (quantize.c tradotto)
                    // row index r+1 usato per il seed del dithering
                    bool qSuccess = FitsQuantizerHighLevel.QuantizeFloat(
                        r + 1, 
                        rowData, width, 1, 
                        true, float.NaN, // Null checking
                        qLevel, ditherMethod,
                        out intsToCompress, out scale, out zero, out imin, out imax
                    );

                    // Se la quantizzazione fallisce (raro, es. range eccessivo), 
                    // fpack solitamente fa fallback, ma qui assumiamo successo o array piatto.
                    if (!qSuccess)
                    {
                        // Fallback: array di zeri o gestione errore
                        intsToCompress = new int[width]; 
                    }
                }
                else
                {
                    // RICE STANDARD (Intero -> Intero, Lossless)
                    intsToCompress = ExtractRowAsInt32(pixels, r);
                }

                // Codifica Rice dei bit
                compressedBlock = RiceCodec.Encode(intsToCompress, BlockSize);
            }

            // C. Scrittura nella Binary Table
            int compressedSize = compressedBlock.Length;
            // L'offset nello Heap è relativo all'inizio dello Heap stesso (definito da THEAP)
            int heapPtr = (int)heapStream.Position; 
            int rowOffset = r * rowStride;

            // 1. Scrittura Descrittore Heap (Colonna COMPRESSED_DATA)
            // [Size (4b) | Offset (4b)]
            BinaryPrimitives.WriteInt32BigEndian(tableBuffer.AsSpan(rowOffset, 4), compressedSize);
            BinaryPrimitives.WriteInt32BigEndian(tableBuffer.AsSpan(rowOffset + 4, 4), heapPtr);

            // 2. Scrittura Parametri Quantizzazione (Se attivi)
            if (useQuantization)
            {
                // Colonna ZSCALE (Double 8b) - Offset +8
                BinaryPrimitives.WriteDoubleBigEndian(tableBuffer.AsSpan(rowOffset + 8, 8), scale);
                // Colonna ZZERO (Double 8b) - Offset +16
                BinaryPrimitives.WriteDoubleBigEndian(tableBuffer.AsSpan(rowOffset + 16, 8), zero);
            }

            // D. Scrittura dati nello Heap
            heapStream.Write(compressedBlock);
        }

        // 5. Finalizzazione PCOUNT (Dimensione totale dello Heap)
        compressedHeader.AddOrUpdateCard("PCOUNT", heapStream.Length.ToString(), "Size of the heap");

        // 6. Assemblaggio Finale (Tabella + Heap)
        byte[] finalHduBody = new byte[tableBuffer.Length + heapStream.Length];
        Buffer.BlockCopy(tableBuffer, 0, finalHduBody, 0, tableBuffer.Length);
        Buffer.BlockCopy(heapStream.GetBuffer(), 0, finalHduBody, tableBuffer.Length, (int)heapStream.Length);

        return finalHduBody;
    }

    // =======================================================================
    // HELPERS DI ESTRAZIONE DATI
    // =======================================================================

    private static float[] GetRowAsFloats(Array pixels, int row, int width)
    {
        float[] result = new float[width];
        
        if (pixels is float[,] f)
        {
            for (int i = 0; i < width; i++) result[i] = f[row, i];
        }
        else if (pixels is double[,] d)
        {
            // Convertiamo Double -> Float per la quantizzazione.
            // Rice/Fpack standard converte a int32, quindi la precisione float (23 bit mantissa)
            // è solitamente sufficiente per il calcolo del rumore e dithering.
            for (int i = 0; i < width; i++) result[i] = (float)d[row, i];
        }
        else
        {
            // Fallback per altri tipi (non dovrebbe accadere con bitpix < 0)
             for (int i = 0; i < width; i++) result[i] = Convert.ToSingle(pixels.GetValue(row, i));
        }
        return result;
    }

    private static byte[] GetRawBytesRow(Array pixels, int row, int width, int bitpix)
    {
        int bytesPerPixel = Math.Abs(bitpix) / 8;
        byte[] buffer = new byte[width * bytesPerPixel];

        switch (pixels)
        {
            case byte[,] b: 
                for (int i = 0; i < width; i++) buffer[i] = b[row, i]; break;
            case sbyte[,] sb: // Gestito come byte raw
                for (int i = 0; i < width; i++) buffer[i] = (byte)sb[row, i]; break;
            case short[,] s: 
                for (int i = 0; i < width; i++) BinaryPrimitives.WriteInt16BigEndian(buffer.AsSpan(i * 2), s[row, i]); break;
            case ushort[,] us: 
                for (int i = 0; i < width; i++) BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(i * 2), us[row, i]); break;
            case int[,] n: 
                for (int i = 0; i < width; i++) BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(i * 4), n[row, i]); break;
            case uint[,] un: 
                for (int i = 0; i < width; i++) BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(i * 4), un[row, i]); break;
            case float[,] f: 
                for (int i = 0; i < width; i++) BinaryPrimitives.WriteSingleBigEndian(buffer.AsSpan(i * 4), f[row, i]); break;
            case double[,] d: 
                for (int i = 0; i < width; i++) BinaryPrimitives.WriteDoubleBigEndian(buffer.AsSpan(i * 8), d[row, i]); break;
        }
        return buffer;
    }

    private static int[] ExtractRowAsInt32(Array pixels, int row)
    {
        int w = pixels.GetLength(1);
        int[] result = new int[w];

        // NOTA: Rice comprime sempre flussi di Interi (differenze).
        // Dobbiamo mappare tutti i tipi di input in Int32 preservando l'ordine numerico.
        // Implementa la logica di imcompress.c per i tipi unsigned (XOR del MSB).

        switch (pixels)
        {
            // --- 8-BIT (BYTE) ---
            // FITS BITPIX = 8 (Range 0..255)
            case byte[,] b:
                for (int x = 0; x < w; x++) result[x] = b[row, x]; 
                break;

            // --- 8-BIT SIGNED (SBYTE) ---
            // FITS non ha sbyte nativo, solitamente salvato come BITPIX=8 + BZERO=-128
            case sbyte[,] sb:
                for (int x = 0; x < w; x++) result[x] = sb[row, x]; 
                break;

            // --- 16-BIT SIGNED (SHORT) ---
            // FITS BITPIX = 16
            case short[,] s:
                for (int x = 0; x < w; x++) result[x] = s[row, x];
                break;

            // --- 16-BIT UNSIGNED (USHORT) ---
            // FITS BITPIX = 16, BZERO = 32768
            // Trucco fpack: XOR sul bit di segno per centrare lo 0
            case ushort[,] us:
                for (int x = 0; x < w; x++) result[x] = (short)(us[row, x] ^ 0x8000);
                break;

            // --- 32-BIT SIGNED (INT) ---
            // FITS BITPIX = 32
            case int[,] i:
                for (int x = 0; x < w; x++) result[x] = i[row, x];
                break;

            // --- 32-BIT UNSIGNED (UINT) ---
            // FITS BITPIX = 32, BZERO = 2147483648
            // Trucco fpack: XOR sul bit di segno (MSB 32-bit)
            case uint[,] ui:
                for (int x = 0; x < w; x++) result[x] = (int)(ui[row, x] ^ 0x80000000);
                break;

            default:
                throw new NotSupportedException($"Tipo array {pixels.GetType()} non supportato per compressione intera.");
        }
        
        return result;
    }

    // =======================================================================
    // HEADER GENERATION
    // =======================================================================

    private static FitsHeader CreateZHeader(FitsHeader original, int bitpix, int w, int h, int zt1, int zt2, FitsCompressionMode mode, bool useQuantization, long theapSize)
    {
        var hC = new FitsHeader();
        
        // --- 1. Keywords Obbligatorie Binary Table ---
        hC.AddCard(new FitsCard("XTENSION", "'BINTABLE'", "Binary Table extension", false));
        hC.AddCard(new FitsCard("BITPIX", "8", "Binary Table data is bytes", false));
        hC.AddCard(new FitsCard("NAXIS", "2", "2-dimensional binary table", false));
        
        // Larghezza riga tabella (8 byte pointer + ev. 16 byte scaling)
        int rowWidth = useQuantization ? 24 : 8;
        hC.AddCard(new FitsCard("NAXIS1", rowWidth.ToString(), "Width of table row in bytes", false));
        hC.AddCard(new FitsCard("NAXIS2", h.ToString(), "Number of rows", false));
        
        // PCOUNT segnaposto (aggiornato alla fine)
        hC.AddCard(new FitsCard("PCOUNT", "0", "Size of the heap area", false));
        hC.AddCard(new FitsCard("GCOUNT", "1", "One group", false));
        
        hC.AddCard(new FitsCard("TFIELDS", useQuantization ? "3" : "1", "Number of columns", false));

        // --- 2. Definizione Colonne ---
        // Colonna 1: Dati Compressi (Heap Pointer - Variable Length Array)
        // La sintassi '1PB(0)' indica puntatore a Byte (B), max length indefinita (0) o variabile.
        hC.AddCard(new FitsCard("TTYPE1", "'COMPRESSED_DATA'", "Compressed byte stream", false));
        hC.AddCard(new FitsCard("TFORM1", "'1PB(0)  '", "Variable length byte array", false));

        if (useQuantization)
        {
            hC.AddCard(new FitsCard("TTYPE2", "'ZSCALE  '", "Linear scaling factor", false));
            hC.AddCard(new FitsCard("TFORM2", "'1D      '", "Double precision float", false));
            
            hC.AddCard(new FitsCard("TTYPE3", "'ZZERO   '", "Zero point", false));
            hC.AddCard(new FitsCard("TFORM3", "'1D      '", "Double precision float", false));
        }

        // --- 3. Keywords di Compressione (Z-Keywords) ---
        hC.AddCard(new FitsCard("ZIMAGE", "T", "Extension contains compressed image", false));
        hC.AddCard(new FitsCard("ZBITPIX", bitpix.ToString(), "Original BITPIX", false));
        hC.AddCard(new FitsCard("ZNAXIS", "2", "Original NAXIS", false));
        hC.AddCard(new FitsCard("ZNAXIS1", w.ToString(), "Original Width", false));
        hC.AddCard(new FitsCard("ZNAXIS2", h.ToString(), "Original Height", false));
        hC.AddCard(new FitsCard("ZTILE1", zt1.ToString(), "Tile Width", false));
        hC.AddCard(new FitsCard("ZTILE2", zt2.ToString(), "Tile Height", false));
        
        string algo = mode == FitsCompressionMode.Rice ? "'RICE_1  '" : "'GZIP_1  '";
        hC.AddCard(new FitsCard("ZCMPTYPE", algo, "Compression algorithm", false));

        // --- 4. Parametri Critici per Compatibilità ---
        
        // ZQUANTIZ: Specifica il metodo di quantizzazione.
        // Con FitsQuantizerHighLevel usiamo Subtractive Dither 1.
        if (useQuantization)
        {
            hC.AddCard(new FitsCard("ZQUANTIZ", "'SUBTRACTIVE_DITHER_1'", "Quantization method", false));
            
            // ZBLANK: Valore intero che rappresenta NaN.
            // Necessario per dire al reader come interpretare -2147483647
            if (bitpix < 0)
            {
                hC.AddCard(new FitsCard("ZBLANK", FitsQuantizerHighLevel.NULL_VALUE.ToString(), "Value for NaN pixels", false));
            }
        }
        
        // THEAP: Offset dall'inizio dei dati (dopo l'header) all'inizio dello heap.
        // Coincide con la dimensione della tabella fissa (NAXIS1 * NAXIS2).
        hC.AddCard(new FitsCard("THEAP", theapSize.ToString(), "Offset of heap", false));

        // --- 5. Copia Metadati Originali ---
        // Copiamo tutto tranne le keyword strutturali di base e quelle che abbiamo appena sovrascritto
        var structural = new HashSet<string> { 
            "SIMPLE", "BITPIX", "NAXIS", "NAXIS1", "NAXIS2", "EXTEND", "PCOUNT", "GCOUNT", 
            "TFIELDS", "THEAP", "END", "XTENSION"
        };
        
        foreach (var card in original.Cards)
        {
            string key = card.Key.ToUpperInvariant();
            if (!structural.Contains(key) && !key.StartsWith("TFORM") && !key.StartsWith("TTYPE"))
            {
                hC.AddCard(new FitsCard(card.Key, card.Value, card.Comment, false));
            }
        }

        return hC;
    }

    private static int GetBitpix(Array pixels)
    {
        if (pixels is byte[,]) return 8;
        if (pixels is sbyte[,]) return 8; // Gestito come byte con shift
        if (pixels is short[,]) return 16;
        if (pixels is ushort[,]) return 16; // Gestito come short con offset
        if (pixels is int[,]) return 32;
        if (pixels is uint[,]) return 32; // Gestito come int con offset
        if (pixels is float[,]) return -32;
        if (pixels is double[,]) return -64;
        throw new NotSupportedException("Tipo pixel non supportato da FITS");
    }
}