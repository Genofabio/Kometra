using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics; 
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

    public async Task<List<FitsHdu>> ReadAllHdusAsync(string path) => await Task.Run(() => {
        try 
        { 
            Debug.WriteLine($"[FITS IO] Apertura file: {Path.GetFileName(path)}");
            using var s = _streamProvider.Open(path);
            return ReadAllHdus(s);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FITS IO] ERRORE CRITICO APERTURA FILE {path}: {ex.Message}");
            return new List<FitsHdu>(); 
        }
    });

    public async Task<FitsHeader?> ReadHeaderAsync(string path) => await Task.Run(() => {
        try { 
            using var s = _streamProvider.Open(path);
            var hdus = ReadAllHdus(s);
            // Restituisce l'header della prima immagine valida (ereditata dal primario)
            var selected = hdus.FirstOrDefault(h => !h.IsEmpty) ?? hdus.FirstOrDefault();
            return selected?.Header;
        } catch { return null; }
    });

    public async Task<Array?> ReadPixelDataAsync(string path) => await Task.Run(() => {
        try { 
            using var s = _streamProvider.Open(path);
            var hdus = ReadAllHdus(s);
            return hdus.FirstOrDefault(h => !h.IsEmpty)?.PixelData;
        } catch { return null; }
    });

    private List<FitsHdu> ReadAllHdus(Stream s)
    {
        var list = new List<FitsHdu>();
        FitsHeader primaryHeader = null; 
        int hduCount = 0;
        
        while (s.Position < s.Length)
        {
            long startPos = s.Position;
            Debug.WriteLine($"[FITS IO] --- Inizio lettura HDU #{hduCount} a pos {startPos} ---");
            
            // 1. Legge l'Header grezzo
            var header = _reader.ReadHeader(s);
            
            // 2. Gestione Ereditarietà (Inheritance)
            if (hduCount == 0)
            {
                primaryHeader = header; // HDU 0 è il contenitore globale dei metadati
            }
            else if (primaryHeader != null)
            {
                // Uniamo i metadati del primario nell'estensione corrente (senza sovrascrivere)
                MergePrimaryMetadata(header, primaryHeader);
            }

            // 3. Analisi Tipo HDU
            string xtension = GetString(header, "XTENSION");
            bool zImageBool = GetBool(header, "ZIMAGE");
            bool isCompressedImage = xtension.Contains("BINTABLE") && zImageBool;
            bool isBintable = xtension.Contains("BINTABLE") && !zImageBool;

            long dataStartPosition = s.Position;

            if (isCompressedImage)
            {
                try 
                {
                    Debug.WriteLine($"[FITS IO] HDU #{hduCount}: Avvio decompressione...");
                    var pixelData = FitsDecompression.DecompressImage(s, header, _reader);
                    
                    // La normalizzazione crea un nuovo header già ordinato con SIMPLE=T e statistiche
                    var normalizedHeader = NormalizeCompressedHeader(header, pixelData);
                    
                    if (pixelData != null && pixelData.Length > 0) 
                        FlipArrayVertical(pixelData);

                    list.Add(new FitsHdu(normalizedHeader, pixelData, false));
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[FITS IO] Errore decompressione HDU #{hduCount}: {ex.Message}");
                    if (s.CanSeek) s.Seek(dataStartPosition, SeekOrigin.Begin);
                    _reader.SkipDataBlock(s, header);
                }
            }
            else if (isBintable)
            {
                Debug.WriteLine($"[FITS IO] HDU #{hduCount}: Tabella dati ignorata.");
                _reader.SkipDataBlock(s, header);
            }
            else
            {
                // Immagine Standard (o Primary Header con dati)
                Debug.WriteLine($"[FITS IO] HDU #{hduCount}: Lettura matrice standard...");
                var data = _reader.ReadMatrix(s, header);
                
                if (data != null && data.Length > 0) 
                {
                    FlipArrayVertical(data);
                }
                
                // Promozione Header: XTENSION -> SIMPLE per rendere l'immagine stand-alone
                var finalHeader = PromoteToPrimaryHeader(header);

                bool isEmpty = data == null || data.Length == 0;
                list.Add(new FitsHdu(finalHeader, data ?? Array.CreateInstance(typeof(byte), 0, 0), isEmpty));
            }
            hduCount++;
        }
        
        Debug.WriteLine($"[FITS IO] Lettura completata. Totale HDU: {list.Count}");
        return list;
    }

    // --- PROMOZIONE HEADER (Fix per errore .Insert) ---
    private FitsHeader PromoteToPrimaryHeader(FitsHeader original)
    {
        var promoted = new FitsHeader();
        
        // SIMPLE=T deve essere obbligatoriamente la prima card
        promoted.AddCard(new FitsCard("SIMPLE", "T", "Standard FITS Image", false));

        foreach (var card in original.Cards)
        {
            string key = card.Key.ToUpperInvariant().Trim();
            
            // Saltiamo chiavi che non appartengono a un'immagine primaria o duplicati
            if (key == "SIMPLE" || key == "XTENSION" || key == "END") continue;
            
            promoted.AddCard(new FitsCard(card.Key, card.Value, card.Comment, false));
        }

        // END deve essere l'ultima
        promoted.AddCard(new FitsCard("END", "", "", false));
        return promoted;
    }

    // --- LOGICA DI FUSIONE HEADER (INHERITANCE) ---
    private void MergePrimaryMetadata(FitsHeader target, FitsHeader source)
    {
        // Chiavi strutturali che non devono mai essere ereditate
        var structuralKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) 
        { 
            "SIMPLE", "XTENSION", "BITPIX", "NAXIS", "PCOUNT", "GCOUNT", 
            "EXTNAME", "EXTVER", "EXTLEVEL", "TFIELDS", "THEAP", "END",
            "BSCALE", "BZERO" 
        };

        foreach (var card in source.Cards)
        {
            string key = card.Key?.ToUpperInvariant().Trim() ?? "";
            if (string.IsNullOrEmpty(key)) continue;

            if (structuralKeys.Contains(key) || key.StartsWith("NAXIS") || key.StartsWith("TFORM") || key.StartsWith("TTYPE"))
                continue;

            // Applica solo se la chiave manca nell'HDU corrente (non sovrascrive)
            if (!target.Cards.Any(c => c.Key.Equals(key, StringComparison.OrdinalIgnoreCase)))
            {
                target.AddCard(new FitsCard(card.Key, card.Value, card.Comment, false));
            }
        }
    }

    // --- NORMALIZZAZIONE HEADER COMPRESSO ---
    private FitsHeader NormalizeCompressedHeader(FitsHeader original, Array pixelData)
    {
        var normalized = new FitsHeader();
        
        // 1. Inizio Header Indipendente
        normalized.AddCard(new FitsCard("SIMPLE", "T", "Converted from Compressed FITS", false));
        
        string zBitpix = GetString(original, "ZBITPIX");
        normalized.AddCard(new FitsCard("BITPIX", string.IsNullOrEmpty(zBitpix) ? "16" : zBitpix, "Original Data Depth", false));
        
        string zNaxis = GetString(original, "ZNAXIS");
        if (int.TryParse(zNaxis, out int dims))
        {
            normalized.AddCard(new FitsCard("NAXIS", zNaxis, "Original Axes", false));
            for (int i = 1; i <= dims; i++)
            {
                string val = GetString(original, $"ZNAXIS{i}");
                normalized.AddCard(new FitsCard($"NAXIS{i}", val, $"Axis {i} Length", false));
            }
        }

        // 2. Copia Metadata (Include metadati già fusi dal primario)
        foreach (var card in original.Cards)
        {
            string key = card.Key.ToUpperInvariant().Trim();

            if (key == "SIMPLE" || key == "XTENSION" || key == "BITPIX" || key == "END" ||
                key.StartsWith("NAXIS") || key.StartsWith("Z") || key.StartsWith("TFORM") || 
                key.StartsWith("TTYPE") || key == "PCOUNT" || key == "GCOUNT" || 
                key == "THEAP" || key == "TFIELDS" || key == "DATAMIN" || 
                key == "DATAMAX" || key == "DATAMEAN")
            {
                continue;
            }
            normalized.AddCard(new FitsCard(card.Key, card.Value, card.Comment, false));
        }

        // 3. Ricalcolo Statistiche Reali
        UpdateHeaderStatistics(normalized, pixelData);

        // 4. History e Chiusura
        string originalAlgo = GetString(original, "ZCMPTYPE");
        string historyMsg = "Decompressed by KomaLab IO";
        if (!string.IsNullOrEmpty(originalAlgo)) historyMsg += $" (Original Algo: {originalAlgo})";
        
        normalized.AddCard(new FitsCard("HISTORY", historyMsg, null, false));
        normalized.AddCard(new FitsCard("END", "", "", false));

        return normalized;
    }

    private void UpdateHeaderStatistics(FitsHeader header, Array data)
    {
        if (data == null || data.Length == 0) return;
        try 
        {
            double min = double.MaxValue, max = double.MinValue, sum = 0;
            long count = 0;
            bool ok = false;

            if (data is float[,] f) { count = f.Length; foreach (float v in f) { if (v < min) min = v; if (v > max) max = v; sum += v; } ok = true; }
            else if (data is int[,] i) { count = i.Length; foreach (int v in i) { if (v < min) min = v; if (v > max) max = v; sum += v; } ok = true; }
            else if (data is short[,] s) { count = s.Length; foreach (short v in s) { if (v < min) min = v; if (v > max) max = v; sum += v; } ok = true; }
            else if (data is double[,] d) { count = d.Length; foreach (double v in d) { if (v < min) min = v; if (v > max) max = v; sum += v; } ok = true; }

            if (ok && count > 0)
            {
                var culture = System.Globalization.CultureInfo.InvariantCulture;
                header.AddCard(new FitsCard("DATAMIN", min.ToString("E6", culture), "Minimum data value", false));
                header.AddCard(new FitsCard("DATAMAX", max.ToString("E6", culture), "Maximum data value", false));
                header.AddCard(new FitsCard("DATAMEAN", (sum / count).ToString("E6", culture), "Mean data value", false));
            }
        }
        catch { }
    }

    // --- HELPERS DI PARSING ---
    private string GetString(FitsHeader h, string key) => 
        h.Cards.FirstOrDefault(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase))?.Value?.Trim()?.ToUpperInvariant() ?? "";

    private bool GetBool(FitsHeader h, string key) => 
        GetString(h, key) == "T";

    // --- SCRITTURA & UTILS ---

    public async Task WriteFileAsync(string path, Array data, FitsHeader header) => await Task.Run(() => {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        _writer.WriteHeader(fs, header);
        _writer.WriteMatrix(fs, data); 
    });

    public async Task CopyFileAsync(string source, string dest) => await Task.Run(() => File.Copy(source, dest, true));

    public string BuildRawPath(string subFolder, string fileName)
    {
        string path = Path.Combine(Path.GetTempPath(), "KomaLab", subFolder);
        if (!Directory.Exists(path)) Directory.CreateDirectory(path);
        return Path.Combine(path, fileName);
    }

    public void TryDeleteFile(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

    private void FlipArrayVertical(Array matrix) {
        switch (matrix) {
            case byte[,] b: FlipMatrix(b); break;
            case short[,] s: FlipMatrix(s); break;
            case int[,] i: FlipMatrix(i); break;
            case float[,] f: FlipMatrix(f); break;
            case double[,] d: FlipMatrix(d); break;
        }
    }

    private void FlipMatrix<T>(T[,] matrix) {
        int h = matrix.GetLength(0); int w = matrix.GetLength(1); 
        for (int y = 0; y < h / 2; y++) {
            int mirrorY = h - 1 - y;
            for (int x = 0; x < w; x++) { 
                T temp = matrix[y, x]; 
                matrix[y, x] = matrix[mirrorY, x]; 
                matrix[mirrorY, x] = temp; 
            }
        }
    }
}