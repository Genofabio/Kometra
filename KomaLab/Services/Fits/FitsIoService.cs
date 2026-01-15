using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using KomaLab.Models.Fits;
using KomaLab.Services.Fits.Engine;

namespace KomaLab.Services.Fits;

/// <summary>
/// Servizio I/O FITS Implementazione.
/// Gestisce la lettura/scrittura separata di Header e Matrici Pixel.
/// </summary>
public class FitsIoService : IFitsIoService
{
    private readonly IFileStreamProvider _streamProvider;
    private readonly IFitsMetadataService _metadataService;
    private readonly FitsReader _reader; 

    public FitsIoService(
        IFileStreamProvider streamProvider, 
        IFitsMetadataService metadataService,
        FitsReader reader)
    {
        _streamProvider = streamProvider ?? throw new ArgumentNullException(nameof(streamProvider));
        _metadataService = metadataService ?? throw new ArgumentNullException(nameof(metadataService));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    // --------------------------------------------------------------------------
    // 1. LETTURA (READ)
    // --------------------------------------------------------------------------

    public async Task<FitsHeader?> ReadHeaderAsync(string path)
    {
        return await Task.Run(() =>
        {
            try
            {
                using var stream = _streamProvider.Open(path);
                return _reader.ReadHeader(stream);
            }
            catch 
            { 
                return null; 
            }
        });
    }

    public async Task<Array?> ReadPixelDataAsync(string path)
    {
        return await Task.Run(() =>
        {
            try
            {
                using Stream stream = _streamProvider.Open(path);
                
                // 1. Leggiamo l'header (serve al reader per sapere dimensioni e bitpix)
                var header = _reader.ReadHeader(stream);
                
                // 2. Leggiamo la matrice grezza
                var rawMatrix = _reader.ReadMatrix(stream, header);

                // 3. FLIP VERTICALE (Cruciale per visualizzazione corretta)
                // In FITS (0,0) è in basso a sinistra. A video è in alto a sinistra.
                if (rawMatrix != null)
                {
                    FlipArrayVertical(rawMatrix);
                }

                return rawMatrix;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[FitsIoService] ReadPixelData Error {path}: {ex.Message}");
                return null;
            }
        });
    }

    // --------------------------------------------------------------------------
    // 2. SCRITTURA (WRITE)
    // --------------------------------------------------------------------------

    public async Task WriteFileAsync(string path, Array pixelData, FitsHeader header)
    {
        await Task.Run(() =>
        {
            // Usiamo FileMode.Create per sovrascrivere completamente il file
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            
            // 1. Scrittura Header (con padding)
            WriteFitsHeader(fs, header);
            
            // 2. Scrittura Dati 
            // Il metodo WriteFitsData gestisce il Reverse Flip (Top-Down -> Bottom-Up)
            WriteFitsData(fs, pixelData);
        });
    }

    public async Task WriteHeaderAsync(string path, FitsHeader newHeader)
    {
        // STRATEGIA SAFE REWRITE:
        // 1. Carica i pixel esistenti (verranno flippati in RAM).
        // 2. Riscrive tutto il file usando il metodo generico WriteFileAsync.
        
        var pixels = await ReadPixelDataAsync(path);
        
        if (pixels == null) 
            throw new FileNotFoundException("Impossibile leggere il file originale per l'aggiornamento dell'header.", path);

        await WriteFileAsync(path, pixels, newHeader);
    }

    // --------------------------------------------------------------------------
    // 3. UTILITY BATCH (BATCH)
    // --------------------------------------------------------------------------

    public async Task<List<string>> BatchSortByDateAsync(IEnumerable<string> paths)
    {
        var pathList = paths.ToList();
        if (pathList.Count <= 1) return pathList;
        try
        {
            var tasks = pathList.Select(async path =>
            {
                var h = await ReadHeaderAsync(path);
                var d = h != null ? _metadataService.GetObservationDate(h) : DateTime.MinValue;
                return new { Path = path, Date = d ?? DateTime.MinValue };
            });
            var res = await Task.WhenAll(tasks);
            return res.OrderBy(x => x.Date).Select(x => x.Path).ToList();
        }
        catch { return pathList; }
    }

    public async Task<(bool IsCompatible, string? Error)> BatchValidateAsync(IEnumerable<string> paths)
    {
        int? fw = null, fh = null;
        foreach (var path in paths)
        {
            var h = await ReadHeaderAsync(path);
            if (h == null) continue;
            int w = h.GetIntValue("NAXIS1");
            int h_img = h.GetIntValue("NAXIS2");

            if (fw == null) { fw = w; fh = h_img; }
            else if (w != fw || h_img != fh)
                return (false, $"Dimensioni mismatch: {Path.GetFileName(path)}");
        }
        return (true, null);
    }

    // --------------------------------------------------------------------------
    // 4. PRIVATE HELPERS: MEMORY FLIP
    // --------------------------------------------------------------------------
    
    

    private void FlipArrayVertical(Array matrix)
    {
        switch (matrix)
        {
            case byte[,] b: FlipMatrix(b); break;
            case short[,] s: FlipMatrix(s); break;
            case int[,] i: FlipMatrix(i); break;
            case float[,] f: FlipMatrix(f); break;
            case double[,] d: FlipMatrix(d); break;
            default: 
                System.Diagnostics.Debug.WriteLine($"Flip non supportato per il tipo {matrix.GetType()}");
                break;
        }
    }

    private void FlipMatrix<T>(T[,] matrix)
    {
        int h = matrix.GetLength(0); 
        int w = matrix.GetLength(1); 

        for (int y = 0; y < h / 2; y++)
        {
            int mirrorY = h - 1 - y;
            for (int x = 0; x < w; x++)
            {
                T temp = matrix[y, x];
                matrix[y, x] = matrix[mirrorY, x];
                matrix[mirrorY, x] = temp;
            }
        }
    }

    // --------------------------------------------------------------------------
    // 5. PRIVATE HELPERS: LOW LEVEL WRITE
    // --------------------------------------------------------------------------

    private void WriteFitsHeader(Stream stream, FitsHeader header)
    {
        int bytesWritten = 0;
        bool endKeyWritten = false;

        foreach (var card in header.Cards) 
        {
            string line = FitsFormatting.PadTo80(card);
            
            if (card.Key.Trim().ToUpper() == "END")
            {
                endKeyWritten = true;
                line = "END".PadRight(80, ' ');
            }

            byte[] bytes = Encoding.ASCII.GetBytes(line);
            stream.Write(bytes, 0, bytes.Length);
            bytesWritten += 80;
        }

        if (!endKeyWritten)
        {
            byte[] endBytes = Encoding.ASCII.GetBytes("END".PadRight(80, ' '));
            stream.Write(endBytes, 0, endBytes.Length);
            bytesWritten += 80;
        }

        int remainder = bytesWritten % 2880;
        if (remainder > 0)
        {
            stream.Write(new byte[2880 - remainder]);
        }
    }

    private void WriteFitsData(Stream stream, Array matrix)
    {
        int height = matrix.GetLength(0);
        int width = matrix.GetLength(1);
        Type type = matrix.GetType().GetElementType()!; 
        
        long totalBytesWritten = 0;

        // Scrittura inversa (Bottom-Up) per rispettare standard FITS
        // partendo da dati in memoria (Top-Down)
        void WritePixelsReverse<T>(T[,] mat, int bytesPerPixel, Action<Span<byte>, T> writerFunc)
        {
            byte[] buffer = new byte[width * bytesPerPixel];
            
            for (int y = height - 1; y >= 0; y--)
            {
                for (int x = 0; x < width; x++)
                {
                    writerFunc(buffer.AsSpan(x * bytesPerPixel), mat[y, x]);
                }
                stream.Write(buffer, 0, buffer.Length);
                totalBytesWritten += buffer.Length;
            }
        }

        if (type == typeof(short)) WritePixelsReverse((short[,])matrix, 2, FitsStreamHelper.WriteInt16);
        else if (type == typeof(int)) WritePixelsReverse((int[,])matrix, 4, FitsStreamHelper.WriteInt32);
        else if (type == typeof(float)) WritePixelsReverse((float[,])matrix, 4, FitsStreamHelper.WriteFloat);
        else if (type == typeof(double)) WritePixelsReverse((double[,])matrix, 8, FitsStreamHelper.WriteDouble);
        else if (type == typeof(byte))
        {
            var mat = (byte[,])matrix;
            byte[] buffer = new byte[width];
            for (int y = height - 1; y >= 0; y--)
            {
                for (int x = 0; x < width; x++) buffer[x] = mat[y, x];
                stream.Write(buffer, 0, buffer.Length);
                totalBytesWritten += buffer.Length;
            }
        }
        else throw new NotSupportedException($"Salvataggio non supportato per pixel {type.Name}");

        long remainder = totalBytesWritten % 2880;
        if (remainder > 0) stream.Write(new byte[2880 - remainder]);
    }
}