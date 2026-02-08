using System;
using System.IO;
using System.Buffers.Binary;
using System.Linq;
using System.Collections.Generic;
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
    // Valore standard FITS per rappresentare NULL/NaN nei dati interi compressi
    private const int FITS_NULL_VALUE = -2147483647;

    public static byte[] CompressImage(Array pixels, FitsHeader originalHeader, FitsCompressionMode mode, out FitsHeader compressedHeader)
    {
        int width = pixels.GetLength(1);
        int height = pixels.GetLength(0);
        int bitpix = GetBitpix(pixels);
        
        // Determiniamo se serve la Quantizzazione (Solo per Rice su Float/Double)
        bool isFloat = bitpix < 0;
        bool useQuantization = (mode == FitsCompressionMode.Rice && isFloat);

        // 1. Definiamo il Tiling (Default fpack: riga per riga)
        // Questo è lo standard più compatibile per immagini astronomiche
        int zTile1 = width;
        int zTile2 = 1; 
        int rowCount = height;

        // 2. Calcolo dimensione Tabella Binaria e THEAP
        // Base: 8 byte (Pointer: 4 Len + 4 Offset)
        // Quantized: 8 byte (Pointer) + 8 byte (ZSCALE) + 8 byte (ZZERO) = 24 byte
        int rowStride = useQuantization ? 24 : 8;
        long theapOffset = (long)rowCount * rowStride;

        // 3. Creiamo l'Header Compresso (Include traduzione ZBSCALE/ZBZERO)
        compressedHeader = CreateCompressedHeader(originalHeader, pixels, width, height, zTile1, zTile2, mode, useQuantization, theapOffset);

        byte[] tableBuffer = new byte[theapOffset];
        using var heapStream = new MemoryStream();

        // Variabile per tracciare la dimensione massima del blocco compresso per TFORM1
        int maxCompressedSize = 0;

        // 4. Ciclo di Compressione Tile (Row-by-Row)
        for (int r = 0; r < rowCount; r++)
        {
            byte[] compressedBlock;
            double scale = 1.0;
            double zero = 0.0;

            if (mode == FitsCompressionMode.Gzip)
            {
                // GZIP LOSSLESS
                byte[] rawBytes = GetRawBytesRow(pixels, r, width, bitpix);
                compressedBlock = GzipCodec.Compress(rawBytes);
            }
            else // RICE
            {
                if (useQuantization)
                {
                    // RICE CON QUANTIZZAZIONE FPACK (Float/Double -> Int32)
                    float[] rowData = GetRowAsFloats(pixels, r, width);

                    // --- FIX CRITICO PER NAN ---
                    float sentinelNull = float.MaxValue;
                    for (int i = 0; i < rowData.Length; i++)
                    {
                        if (float.IsNaN(rowData[i]) || float.IsInfinity(rowData[i]))
                        {
                            rowData[i] = sentinelNull;
                        }
                    }

                    // Parametri di quantizzazione fpack default
                    float qLevel = 4.0f; 
                    int ditherMethod = FitsQuantizerHighLevel.SUBTRACTIVE_DITHER_1;
                    int imin, imax;
                    int[] intsToCompress;

                    bool qSuccess = FitsQuantizerHighLevel.QuantizeFloat(
                        r + 1, 
                        rowData, width, 1, 
                        true, sentinelNull,
                        qLevel, ditherMethod,
                        out intsToCompress, out scale, out zero, out imin, out imax
                    );

                    if (!qSuccess) intsToCompress = new int[width]; // Fallback

                    // I dati quantizzati sono sempre int32
                    compressedBlock = RiceCodec.Encode(intsToCompress, BlockSize);
                }
                else
                {
                    // RICE STANDARD (Lossless Integer)
                    if (pixels is byte[,] || pixels is sbyte[,])
                    {
                        byte[] row = ExtractRowAsByte(pixels, r);
                        compressedBlock = RiceCodec.Encode(row, BlockSize);
                    }
                    else if (pixels is short[,] || pixels is ushort[,])
                    {
                        short[] row = ExtractRowAsShort(pixels, r);
                        compressedBlock = RiceCodec.Encode(row, BlockSize);
                    }
                    else
                    {
                        int[] row = ExtractRowAsInt32(pixels, r);
                        compressedBlock = RiceCodec.Encode(row, BlockSize);
                    }
                }
            }

            // Aggiorna max block size per TFORM1
            if (compressedBlock.Length > maxCompressedSize)
                maxCompressedSize = compressedBlock.Length;

            // C. Scrittura nella Binary Table (Struttura fissa)
            int compressedSize = compressedBlock.Length;
            int heapPtr = (int)heapStream.Position; 
            int rowOffset = r * rowStride;

            // 1. Scrittura Descrittore Heap (Lunghezza + Offset)
            BinaryPrimitives.WriteInt32BigEndian(tableBuffer.AsSpan(rowOffset, 4), compressedSize);
            BinaryPrimitives.WriteInt32BigEndian(tableBuffer.AsSpan(rowOffset + 4, 4), heapPtr);

            // 2. Scrittura Parametri Quantizzazione (Se presenti)
            if (useQuantization)
            {
                BinaryPrimitives.WriteDoubleBigEndian(tableBuffer.AsSpan(rowOffset + 8, 8), scale);
                BinaryPrimitives.WriteDoubleBigEndian(tableBuffer.AsSpan(rowOffset + 16, 8), zero);
            }

            // D. Scrittura dati compressi nello Heap
            heapStream.Write(compressedBlock);
        }

        // 5. Finalizzazione Header
        // Aggiorna PCOUNT con la dimensione reale totale dello heap
        compressedHeader.AddOrUpdateCard("PCOUNT", heapStream.Length.ToString(), "Size of the heap");
        
        // [FIX] Aggiorna TFORM1 con la dimensione massima reale del blocco
        compressedHeader.AddOrUpdateCard("TFORM1", $"1PB({maxCompressedSize})", "Variable length byte array");

        // 6. Assemblaggio Finale: [Table Structure] + [Heap Data]
        byte[] finalHduBody = new byte[tableBuffer.Length + heapStream.Length];
        Buffer.BlockCopy(tableBuffer, 0, finalHduBody, 0, tableBuffer.Length);
        Buffer.BlockCopy(heapStream.GetBuffer(), 0, finalHduBody, tableBuffer.Length, (int)heapStream.Length);

        return finalHduBody;
    }

    // =======================================================================
    // HELPERS DI ESTRAZIONE DATI
    // =======================================================================

    private static byte[] ExtractRowAsByte(Array pixels, int row)
    {
        int w = pixels.GetLength(1);
        byte[] result = new byte[w];
        if (pixels is byte[,] b)
        {
            for (int x = 0; x < w; x++) result[x] = b[row, x];
        }
        else if (pixels is sbyte[,] sb)
        {
            for (int x = 0; x < w; x++) result[x] = (byte)((byte)sb[row, x] ^ 0x80);
        }
        return result;
    }

    private static short[] ExtractRowAsShort(Array pixels, int row)
    {
        int w = pixels.GetLength(1);
        short[] result = new short[w];
        if (pixels is short[,] s)
        {
            for (int x = 0; x < w; x++) result[x] = s[row, x];
        }
        else if (pixels is ushort[,] us)
        {
            // Conversione USHORT -> SHORT per compressione RICE (segno gestito via XOR)
            for (int x = 0; x < w; x++) result[x] = (short)(us[row, x] ^ 0x8000);
        }
        return result;
    }

    private static int[] ExtractRowAsInt32(Array pixels, int row)
    {
        int w = pixels.GetLength(1);
        int[] result = new int[w];
        switch (pixels)
        {
            case int[,] i:
                for (int x = 0; x < w; x++) result[x] = i[row, x];
                break;
            case uint[,] ui:
                for (int x = 0; x < w; x++) result[x] = (int)(ui[row, x] ^ 0x80000000);
                break;
            default:
                throw new NotSupportedException($"Tipo non supportato per 32-bit int: {pixels.GetType()}");
        }
        return result;
    }

    private static float[] GetRowAsFloats(Array pixels, int row, int width)
    {
        float[] result = new float[width];
        if (pixels is float[,] f)
        {
            for (int i = 0; i < width; i++) result[i] = f[row, i];
        }
        else if (pixels is double[,] d)
        {
            for (int i = 0; i < width; i++) result[i] = (float)d[row, i];
        }
        else
        {
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
            case sbyte[,] sb: 
                for (int i = 0; i < width; i++) buffer[i] = (byte)(sb[row, i] ^ 0x80); break;
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

    // =======================================================================
    // HEADER GENERATION (TRADUZIONE HEADER STANDARD -> HEADER COMPRESSO)
    // =======================================================================

    private static FitsHeader CreateCompressedHeader(FitsHeader original, Array pixels, int w, int h, int zt1, int zt2, FitsCompressionMode mode, bool useQuantization, long theapSize)
    {
        var hC = new FitsHeader();
        int originalBitpix = GetBitpix(pixels);

        // ---------------------------------------------------------
        // 1. STRUTTURA BINTABLE
        // ---------------------------------------------------------
        hC.AddCard(new FitsCard("XTENSION", "BINTABLE", "Binary Table extension", false));
        hC.AddCard(new FitsCard("BITPIX", "8", "Binary Table data is bytes", false));
        hC.AddCard(new FitsCard("NAXIS", "2", "2-dimensional binary table", false));

        int rowWidth = useQuantization ? 24 : 8;
        hC.AddCard(new FitsCard("NAXIS1", rowWidth.ToString(), "Width of table row in bytes", false));
        hC.AddCard(new FitsCard("NAXIS2", h.ToString(), "Number of rows", false));

        hC.AddCard(new FitsCard("PCOUNT", "0", "Size of the heap area", false)); // Aggiornato dopo
        hC.AddCard(new FitsCard("GCOUNT", "1", "One group", false));
        hC.AddCard(new FitsCard("TFIELDS", useQuantization ? "3" : "1", "Number of columns", false));

        hC.AddCard(new FitsCard("TTYPE1", "COMPRESSED_DATA", "Compressed byte stream", false));
        hC.AddCard(new FitsCard("TFORM1", "1PB(0)", "Variable length byte array", false));

        if (useQuantization)
        {
            hC.AddCard(new FitsCard("TTYPE2", "ZSCALE", "Linear scaling factor", false));
            hC.AddCard(new FitsCard("TFORM2", "1D", "Double precision float", false));
            hC.AddCard(new FitsCard("TTYPE3", "ZZERO", "Zero point", false));
            hC.AddCard(new FitsCard("TFORM3", "1D", "Double precision float", false));
        }

        // ---------------------------------------------------------
        // 2. MAPPATURA Z-KEYWORDS
        // ---------------------------------------------------------
        hC.AddCard(new FitsCard("EXTNAME", "COMPRESSED_IMAGE", "Name of this extension", false));
        
        // [AGGIUNTO] Chiave mancante nel tuo file precedente
        hC.AddCard(new FitsCard("ZSIMPLE", "T", "Uncompressed header is SIMPLE", false));

        hC.AddCard(new FitsCard("ZIMAGE", "T", "Extension contains compressed image", false));
        hC.AddCard(new FitsCard("ZBITPIX", originalBitpix.ToString(), "Original BITPIX", false));
        hC.AddCard(new FitsCard("ZNAXIS", "2", "Original NAXIS", false));
        hC.AddCard(new FitsCard("ZNAXIS1", w.ToString(), "Original Width", false));
        hC.AddCard(new FitsCard("ZNAXIS2", h.ToString(), "Original Height", false));
        
        hC.AddCard(new FitsCard("ZTILE1", zt1.ToString(), "Tile Width", false));
        hC.AddCard(new FitsCard("ZTILE2", zt2.ToString(), "Tile Height", false));

        string algo = mode == FitsCompressionMode.Rice ? "RICE_1" : "GZIP_1";
        hC.AddCard(new FitsCard("ZCMPTYPE", algo, "Compression algorithm", false));

        if (mode == FitsCompressionMode.Rice)
        {
            hC.AddCard(new FitsCard("ZNAME1", "BLOCKSIZE", "Compression block size", false));
            hC.AddCard(new FitsCard("ZVAL1", "32", "Pixels per block", false));

            int bytePix = useQuantization ? 4 : Math.Abs(originalBitpix) / 8;
            hC.AddCard(new FitsCard("ZNAME2", "BYTEPIX", "Bytes per pixel", false));
            hC.AddCard(new FitsCard("ZVAL2", bytePix.ToString(), "Bytes per pixel in original", false));
        }

        if (useQuantization)
        {
            hC.AddCard(new FitsCard("ZQUANTIZ", "SUBTRACTIVE_DITHER_1", "Quantization method", false));
            if (originalBitpix < 0)
            {
                hC.AddCard(new FitsCard("ZBLANK", FITS_NULL_VALUE.ToString(), "Value for NaN pixels", false));
            }
        }
        
        hC.AddCard(new FitsCard("ZBLOCKED", "T", "True if blocked", false));

        // ---------------------------------------------------------
        // 3. GESTIONE CRITICA BSCALE/BZERO (Mapping Tipi Unsigned)
        // ---------------------------------------------------------
        Type t = pixels.GetType().GetElementType();
        if (t == typeof(ushort))
        {
            hC.AddCard(new FitsCard("ZBSCALE", "1.0", "Linear scaling factor", false));
            hC.AddCard(new FitsCard("ZBZERO", "32768.0", "Zero point", false));
        }
        else if (t == typeof(uint))
        {
            hC.AddCard(new FitsCard("ZBSCALE", "1.0", "Linear scaling factor", false));
            hC.AddCard(new FitsCard("ZBZERO", "2147483648.0", "Zero point", false));
        }
        
        hC.AddCard(new FitsCard("THEAP", theapSize.ToString(), "Offset of heap", false));

        // ---------------------------------------------------------
        // 4. COPIA METADATI UTENTE
        // ---------------------------------------------------------
        // [MODIFICATO] Ho rimosso BSCALE e BZERO dalla blacklist!
        // Ora verranno copiati dall'originale se presenti.
        var structural = new HashSet<string> {
            "SIMPLE", "XTENSION", "BITPIX", "NAXIS", "NAXIS1", "NAXIS2", "NAXIS3",
            "PCOUNT", "GCOUNT", "GROUPS", "TFIELDS", "THEAP", "END", "EXTEND",
            // "BSCALE", "BZERO", <-- RIMOSSI DALLA LISTA NERA
            "BUNIT", "BLANK", "DATAMAX", "DATAMIN",
            "CHECKSUM", "DATASUM",
            "ZIMAGE", "ZCMPTYPE", "ZBITPIX", "ZNAXIS", "ZNAXIS1", "ZNAXIS2", "ZTILE1", "ZTILE2",
            "ZNAME1", "ZVAL1", "ZNAME2", "ZVAL2", "ZQUANTIZ", "ZBLANK", "ZBSCALE", "ZBZERO", 
            "ZBLOCKED", "EXTNAME", "ZSIMPLE", 
            "TTYPE1", "TFORM1", "TTYPE2", "TFORM2", "TTYPE3", "TFORM3"
        };

        foreach (var card in original.Cards)
        {
            string key = card.Key.ToUpperInvariant().Trim();
            if (!structural.Contains(key))
            {
                hC.AddCard(new FitsCard(card.Key, card.Value, card.Comment, false));
            }
        }

        return hC;
    }

    private static int GetBitpix(Array pixels)
    {
        if (pixels is byte[,]) return 8;
        if (pixels is sbyte[,]) return 8; 
        if (pixels is short[,]) return 16;
        if (pixels is ushort[,]) return 16; 
        if (pixels is int[,]) return 32;
        if (pixels is uint[,]) return 32; 
        if (pixels is float[,]) return -32;
        if (pixels is double[,]) return -64;
        throw new NotSupportedException("Tipo pixel non supportato da FITS");
    }
}