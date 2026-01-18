using System;
using System.IO;
using System.Threading.Tasks;
using KomaLab.Infrastructure;
using KomaLab.Models.Fits;
using KomaLab.Models.Fits.Structure;

namespace KomaLab.Services.Fits.IO;

public class FitsIoService : IFitsIoService
{
    private readonly IFileStreamProvider _streamProvider;
    private readonly FitsReader _reader;
    private readonly FitsWriter _writer; // <--- Nuova Dipendenza

    public FitsIoService(
        IFileStreamProvider streamProvider, 
        FitsReader reader, 
        FitsWriter writer)
    {
        _streamProvider = streamProvider ?? throw new ArgumentNullException(nameof(streamProvider));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
    }

    // =======================================================================
    // 1. OPERAZIONI DI LETTURA (I/O READ)
    // =======================================================================

    public async Task<FitsHeader?> ReadHeaderAsync(string path) => await Task.Run(() => {
        try 
        { 
            using var s = _streamProvider.Open(path); 
            return _reader.ReadHeader(s); 
        }
        catch { return null; }
    });

    public async Task<Array?> ReadPixelDataAsync(string path) => await Task.Run(() => {
        try { 
            using var s = _streamProvider.Open(path);
            var h = _reader.ReadHeader(s);
            var m = _reader.ReadMatrix(s, h);
            
            // Flip per visualizzazione (Top-Down)
            if (m != null) FlipArrayVertical(m);
            return m;
        } 
        catch { return null; }
    });

    // =======================================================================
    // 2. OPERAZIONI DI SCRITTURA (I/O WRITE)
    // =======================================================================

    public async Task WriteFileAsync(string path, Array data, FitsHeader header) => await Task.Run(() => {
        // FileMode.Create sovrascrive il file se esiste
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        
        // Deleghiamo interamente al Writer
        // Il Writer gestisce: Padding 80 char, blocco 2880 header
        _writer.WriteHeader(fs, header);

        // Il Writer gestisce: Reverse Flip (Bottom-Up), Endianness, Padding dati
        _writer.WriteMatrix(fs, data); 
    });

    // =======================================================================
    // 3. GESTIONE FILE SYSTEM E PERCORSI
    // =======================================================================

    public async Task CopyFileAsync(string source, string dest) => 
        await Task.Run(() => File.Copy(source, dest, true));

    public string BuildRawPath(string subFolder, string fileName)
    {
        string path = Path.Combine(Path.GetTempPath(), "KomaLab", subFolder);
        
        if (!Directory.Exists(path)) 
            Directory.CreateDirectory(path);
            
        return Path.Combine(path, fileName);
    }

    public void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* Silent */ }
    }

    // =======================================================================
    // 4. HELPERS PRIVATI (Manipolazione Memoria)
    // =======================================================================

    // Manteniamo questo metodo perché serve per il READ (visualizzazione)
    // Il Write usa un flip on-the-fly dentro FitsWriter per non duplicare la RAM.
    private void FlipArrayVertical(Array matrix) 
    { 
        switch (matrix)
        {
            case byte[,] b: FlipMatrix(b); break;
            case short[,] s: FlipMatrix(s); break;
            case int[,] i: FlipMatrix(i); break;
            case float[,] f: FlipMatrix(f); break;
            case double[,] d: FlipMatrix(d); break;
            default: throw new NotSupportedException($"Flip non supportato per {matrix.GetType()}");
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
}