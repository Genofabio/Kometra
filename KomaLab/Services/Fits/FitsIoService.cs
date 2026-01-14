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
/// Implementazione Enterprise del servizio I/O FITS.
/// Esegue il FLIP VERTICALE al caricamento per normalizzare le coordinate (Top-Left 0,0).
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

    // --- CARICAMENTO ---

    public async Task<FitsImageData?> LoadAsync(string path)
    {
        return await Task.Run(() =>
        {
            try
            {
                using Stream stream = _streamProvider.Open(path);
                var header = _reader.ReadHeader(stream);
                var imageData = _reader.ReadImage(stream, header);
                
                // MODIFICA CRUCIALE: FLIP VERTICALE IMMEDIATO
                // I dati FITS arrivano Bottom-Up (riga 0 = fondo).
                // Li ribaltiamo subito per averli Top-Down (riga 0 = cima), 
                // così il resto dell'app lavora in coordinate schermo naturali.
                if (imageData != null)
                {
                    FlipImageDataVertical(imageData);
                }

                return imageData;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[FitsIoService] Errore LoadAsync {path}: {ex.Message}");
                return null;
            }
        });
    }

    public async Task<FitsHeader?> ReadHeaderOnlyAsync(string path)
    {
        return await Task.Run(() =>
        {
            try
            {
                using var stream = _streamProvider.Open(path);
                return _reader.ReadHeader(stream);
            }
            catch { return null; }
        });
    }

    // --- SALVATAGGIO ---

    public async Task SaveAsync(FitsImageData data, string path)
    {
        await Task.Run(() =>
        {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            
            // 1. HEADER
            WriteFitsHeader(fs, data.FitsHeader);

            // 2. DATI
            // Dato che in memoria abbiamo i dati "Dritti" (Top-Down),
            // dobbiamo salvarli usando il ciclo inverso per rispettare lo standard FITS.
            WriteFitsData(fs, data);
        });
    }

    // --- HELPER PER IL FLIP IN MEMORIA ---
    
    private void FlipImageDataVertical(FitsImageData data)
    {
        // Poiché RawData è un Array generico, dobbiamo gestire i tipi concreti
        // per poter scambiare le righe.
        
        if (data.RawData is byte[,] b) FlipMatrix(b, data.Width, data.Height);
        else if (data.RawData is short[,] s) FlipMatrix(s, data.Width, data.Height);
        else if (data.RawData is int[,] i) FlipMatrix(i, data.Width, data.Height);
        else if (data.RawData is float[,] f) FlipMatrix(f, data.Width, data.Height);
        else if (data.RawData is double[,] d) FlipMatrix(d, data.Width, data.Height);
        // Se aggiungi altri tipi (es. uint16), aggiungi qui il caso
    }

    private void FlipMatrix<T>(T[,] matrix, int w, int h)
    {
        // Scambia le righe speculari (0 con h-1, 1 con h-2, ecc.)
        // Ci fermiamo a h/2 altrimenti le riscambiamo di nuovo.
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

    // --- LOGICA DI SCRITTURA (Invariata, è già corretta per Top-Down memory) ---

    private void WriteFitsHeader(Stream stream, FitsHeader header)
    {
        int bytesWritten = 0;
        foreach (var card in header.Cards)
        {
            string line = FitsFormatting.PadTo80(card);
            byte[] bytes = Encoding.ASCII.GetBytes(line);
            stream.Write(bytes, 0, bytes.Length);
            bytesWritten += 80;
        }

        string endLine = "END".PadRight(80, ' ');
        byte[] endBytes = Encoding.ASCII.GetBytes(endLine);
        stream.Write(endBytes, 0, endBytes.Length);
        bytesWritten += 80;

        int remainder = bytesWritten % 2880;
        if (remainder > 0) stream.Write(new byte[2880 - remainder]);
    }

    private void WriteFitsData(Stream stream, FitsImageData data)
    {
        Type type = data.PixelType;
        int width = data.Width;
        int height = data.Height;
        long totalBytesWritten = 0;

        // Helper che scrive dall'ultima riga alla prima (height-1 -> 0)
        // Corretto perché in memoria abbiamo l'immagine DRITTA (0=Top),
        // ma il FITS vuole il primo byte come FONDO.
        void WritePixelsReverse<T>(T[,] matrix, int bytesPerPixel, Action<Span<byte>, T> writerFunc)
        {
            byte[] buffer = new byte[width * bytesPerPixel];
            for (int y = height - 1; y >= 0; y--)
            {
                for (int x = 0; x < width; x++)
                {
                    writerFunc(buffer.AsSpan(x * bytesPerPixel), matrix[y, x]);
                }
                stream.Write(buffer, 0, buffer.Length);
                totalBytesWritten += buffer.Length;
            }
        }

        if (type == typeof(short)) WritePixelsReverse(data.GetData<short>(), 2, FitsStreamHelper.WriteInt16);
        else if (type == typeof(int)) WritePixelsReverse(data.GetData<int>(), 4, FitsStreamHelper.WriteInt32);
        else if (type == typeof(float)) WritePixelsReverse(data.GetData<float>(), 4, FitsStreamHelper.WriteFloat);
        else if (type == typeof(double)) WritePixelsReverse(data.GetData<double>(), 8, FitsStreamHelper.WriteDouble);
        else if (type == typeof(byte))
        {
            var mat = data.GetData<byte>();
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

    // --- BATCH HELPERS ---

    public async Task<List<string>> PrepareBatchAsync(IEnumerable<string> paths)
    {
        var pathList = paths.ToList();
        if (pathList.Count <= 1) return pathList;
        try
        {
            var tasks = pathList.Select(async path =>
            {
                var h = await ReadHeaderOnlyAsync(path);
                var d = h != null ? _metadataService.GetObservationDate(h) : DateTime.MinValue;
                return new { Path = path, Date = d ?? DateTime.MinValue };
            });
            var res = await Task.WhenAll(tasks);
            return res.OrderBy(x => x.Date).Select(x => x.Path).ToList();
        }
        catch { return pathList; }
    }

    public async Task<(bool IsCompatible, string? Error)> ValidateCompatibilityAsync(IEnumerable<string> paths)
    {
        int? fw = null, fh = null;
        foreach (var path in paths)
        {
            var h = await ReadHeaderOnlyAsync(path);
            if (h == null) continue;
            int w = h.GetIntValue("NAXIS1");
            int h_img = h.GetIntValue("NAXIS2");

            if (fw == null) { fw = w; fh = h_img; }
            else if (w != fw || h_img != fh)
                return (false, $"Dimensioni mismatch: {Path.GetFileName(path)}");
        }
        return (true, null);
    }
}