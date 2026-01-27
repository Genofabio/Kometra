using System;
using System.Collections.Generic; // Necessario per List<>
using System.IO;
using System.Linq; // Necessario per FirstOrDefault
using System.Threading.Tasks;
using KomaLab.Infrastructure;
using KomaLab.Models.Fits;
using KomaLab.Models.Fits.Structure;

namespace KomaLab.Services.Fits.IO;

public class FitsIoService : IFitsIoService
{
    private readonly IFileStreamProvider _streamProvider;
    private readonly FitsReader _reader;
    private readonly FitsWriter _writer; 

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
    // 1. OPERAZIONI DI LETTURA (FULL MEF SUPPORT)
    // =======================================================================

    // NUOVO: Legge tutte le estensioni (HDU) per popolare il FitsDataPackage completo
    public async Task<List<FitsHdu>> ReadAllHdusAsync(string path) => await Task.Run(() => {
        try 
        { 
            using var s = _streamProvider.Open(path);
            return ReadAllHdus(s);
        }
        catch { return new List<FitsHdu>(); }
    });

    // Mantenuto per retrocompatibilità: restituisce l'header della prima immagine valida
    public async Task<FitsHeader?> ReadHeaderAsync(string path) => await Task.Run(() => {
        try 
        { 
            using var s = _streamProvider.Open(path);
            var hdus = ReadAllHdus(s);
            // Restituisce l'header della prima immagine non vuota, oppure il primario se tutto vuoto
            return hdus.FirstOrDefault(h => !h.IsEmpty)?.Header ?? hdus.FirstOrDefault()?.Header;
        }
        catch { return null; }
    });

    // Mantenuto per retrocompatibilità: restituisce i pixel della prima immagine valida
    public async Task<Array?> ReadPixelDataAsync(string path) => await Task.Run(() => {
        try 
        { 
            using var s = _streamProvider.Open(path);
            var hdus = ReadAllHdus(s);
            // Restituisce i pixel della prima immagine non vuota
            return hdus.FirstOrDefault(h => !h.IsEmpty)?.PixelData;
        } 
        catch { return null; }
    });

    // --- Metodo helper sincrono per il loop di lettura ---
    private List<FitsHdu> ReadAllHdus(Stream s)
    {
        var list = new List<FitsHdu>();
        
        // Loop attraverso tutto il file
        while (s.Position < s.Length)
        {
            // 1. Legge l'Header
            var header = _reader.ReadHeader(s);
            
            // 2. Determina il tipo di estensione
            string xtension = GetString(header, "XTENSION");
            bool isTable = xtension.Contains("TABLE");

            if (isTable)
            {
                // Le tabelle le saltiamo per ora (Skip dei dati)
                // Nota: FitsReader deve avere il metodo SkipDataBlock pubblico
                _reader.SkipDataBlock(s, header);
            }
            else
            {
                // È un'immagine (o Primary vuoto) -> Leggiamo i pixel
                // Se NAXIS=0, ReadMatrix ritorna un array vuoto e gestisce lo skip
                var data = _reader.ReadMatrix(s, header);
                
                // Flip verticale per visualizzazione (FITS è bottom-up)
                if (data != null && data.Length > 0) FlipArrayVertical(data);

                bool isEmpty = data == null || data.Length == 0;
                
                // Creiamo l'HDU e lo aggiungiamo alla lista
                list.Add(new FitsHdu(header, data ?? Array.CreateInstance(typeof(byte), 0, 0), isEmpty));
            }
        }
        return list;
    }

    private string GetString(FitsHeader h, string key)
    {
         var c = h.Cards.FirstOrDefault(x => x.Key == key);
         return c?.Value?.Trim()?.ToUpperInvariant() ?? "";
    }

    // =======================================================================
    // 2. OPERAZIONI DI SCRITTURA (I/O WRITE)
    // =======================================================================

    public async Task WriteFileAsync(string path, Array data, FitsHeader header) => await Task.Run(() => {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        _writer.WriteHeader(fs, header);
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
        if (!Directory.Exists(path)) Directory.CreateDirectory(path);
        return Path.Combine(path, fileName);
    }

    public void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* Silent */ }
    }

    // =======================================================================
    // 4. HELPERS PRIVATI (Flip)
    // =======================================================================

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