using System;
using System.IO;
using System.Threading.Tasks;
using KomaLab.Infrastructure;
using KomaLab.Models.Fits;

namespace KomaLab.Services.Fits.IO;

public class FitsIoService : IFitsIoService
{
    private readonly IFileStreamProvider _streamProvider;
    private readonly FitsReader _reader; 

    public FitsIoService(IFileStreamProvider streamProvider, FitsReader reader)
    {
        _streamProvider = streamProvider ?? throw new ArgumentNullException(nameof(streamProvider));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
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
            
            if (m != null) FlipArrayVertical(m);
            return m;
        } 
        catch { return null; }
    });

    // =======================================================================
    // 2. OPERAZIONI DI SCRITTURA (I/O WRITE)
    // =======================================================================

    public async Task WriteFileAsync(string path, Array data, FitsHeader header) => await Task.Run(() => {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        
        WriteFitsHeader(fs, header);
        // Nota: WriteFitsData deve gestire internamente il flip inverso 
        // per riportare i dati allo standard FITS (Bottom-Up)
        WriteFitsData(fs, data); 
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
        try 
        { 
            if (File.Exists(path)) File.Delete(path); 
        } 
        catch 
        { 
            // Logging silenzioso o delegato
        }
    }

    // =======================================================================
    // 4. HELPERS PRIVATI (LOGICA BINARIA E TRASFORMAZIONI)
    // =======================================================================

    private void FlipArrayVertical(Array matrix) 
    { 
        /* Logica per invertire l'ordine delle righe (Bottom-Up -> Top-Down) */ 
    }

    private void WriteFitsHeader(Stream s, FitsHeader h) 
    { 
        /* Logica di formattazione e scrittura blocchi header (2880 byte) */ 
    }

    private void WriteFitsData(Stream s, Array m) 
    { 
        /* Logica di conversione Endianness e scrittura pixel con padding */ 
    }
}