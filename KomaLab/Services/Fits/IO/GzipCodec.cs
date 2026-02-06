using System;
using System.IO;
using System.IO.Compression;

namespace KomaLab.Services.Fits.IO;

/// <summary>
/// Codec per la compressione GZIP utilizzata nello standard FITS (GZIP_1).
/// Include un fallback per la decompressione Deflate nel caso di header Gzip mancanti.
/// </summary>
public static class GzipCodec
{
    /// <summary>
    /// Decomprime un array di byte utilizzando GZip o Deflate come fallback.
    /// </summary>
    public static byte[] Decompress(byte[] input)
    {
        if (input == null || input.Length == 0) 
            return Array.Empty<byte>();

        try
        {
            using var msInput = new MemoryStream(input);
            using var gzip = new GZipStream(msInput, CompressionMode.Decompress);
            using var msOutput = new MemoryStream();
            gzip.CopyTo(msOutput);
            return msOutput.ToArray();
        }
        catch
        {
            // Fallback: Alcuni software (es. vecchie versioni di fpack o software legacy) 
            // producono flussi deflate senza il magic number Gzip standard (1F 8B).
            try
            {
                using var msInput = new MemoryStream(input);
                using var deflate = new DeflateStream(msInput, CompressionMode.Decompress);
                using var msOutput = new MemoryStream();
                deflate.CopyTo(msOutput);
                return msOutput.ToArray();
            }
            catch
            {
                // In caso di fallimento totale, restituiamo un array vuoto per non rompere il loop batch
                return Array.Empty<byte>();
            }
        }
    }

    /// <summary>
    /// Comprime un array di byte in formato GZip standard.
    /// </summary>
    public static byte[] Compress(byte[] input)
    {
        if (input == null || input.Length == 0) 
            return Array.Empty<byte>();

        using var msOutput = new MemoryStream();
        // Nota: CompressionLevel.Optimal è la scelta corretta per FITS batch 
        // poiché riduce al minimo il peso dei file finali.
        using (var gzip = new GZipStream(msOutput, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(input, 0, input.Length);
        } // Il Dispose del GZipStream garantisce il Flush finale dei blocchi.
        
        return msOutput.ToArray();
    }
}