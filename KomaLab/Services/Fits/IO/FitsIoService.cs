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
        int hduCount = 0;
        
        while (s.Position < s.Length)
        {
            long startPos = s.Position;
            Debug.WriteLine($"[FITS IO] --- Inizio lettura HDU #{hduCount} a pos {startPos} ---");
            
            var header = _reader.ReadHeader(s);
            
            string xtension = GetString(header, "XTENSION");
            bool zImageBool = GetBool(header, "ZIMAGE");
            bool isBintable = xtension.Contains("BINTABLE");
            bool isCompressedImage = isBintable && zImageBool;

            if (isCompressedImage) Debug.WriteLine($"[FITS IO] HDU #{hduCount}: Immagine Compressa.");
            else if (isBintable) Debug.WriteLine($"[FITS IO] HDU #{hduCount}: Tabella Dati (Ignorata).");
            else Debug.WriteLine($"[FITS IO] HDU #{hduCount}: Immagine Standard.");

            long dataStartPosition = s.Position;

            if (isCompressedImage)
            {
                try 
                {
                    // A. Decompressione
                    var pixelData = FitsDecompression.DecompressImage(s, header, _reader);
                    Debug.WriteLine($"[FITS IO] Decompressione OK. Pixel: {pixelData.Length}");
                    
                    // B. Normalizzazione Header
                    var normalizedHeader = NormalizeCompressedHeader(header, pixelData);
                    
                    if (pixelData != null && pixelData.Length > 0) 
                        FlipArrayVertical(pixelData);

                    list.Add(new FitsHdu(normalizedHeader, pixelData, false));
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[FITS IO] ERRORE DECOMPRESSIONE: {ex.Message}");
                    if (s.CanSeek) s.Seek(dataStartPosition, SeekOrigin.Begin);
                    _reader.SkipDataBlock(s, header);
                }
            }
            else if (isBintable)
            {
                _reader.SkipDataBlock(s, header);
            }
            else
            {
                // C. Immagine Standard -> RAW
                Debug.WriteLine("[FITS IO] Lettura matrice RAW standard...");
                var data = _reader.ReadMatrix(s, header);
                
                if (data != null && data.Length > 0) 
                {
                    Debug.WriteLine($"[FITS IO] Letti {data.Length} pixel RAW.");
                    FlipArrayVertical(data);
                }
                
                bool isEmpty = data == null || data.Length == 0;
                list.Add(new FitsHdu(header, data ?? Array.CreateInstance(typeof(byte), 0, 0), isEmpty));
            }
            hduCount++;
        }
        
        Debug.WriteLine($"[FITS IO] Lettura completata. Totale HDU: {list.Count}");
        return list;
    }

    // --- NORMALIZZAZIONE HEADER ---
    private FitsHeader NormalizeCompressedHeader(FitsHeader original, Array pixelData)
    {
        var normalized = new FitsHeader();
        
        // 1. Chiavi Strutturali Standard
        normalized.AddCard(new FitsCard("SIMPLE", "T", "Converted from Compressed FITS", false));
        
        string zBitpix = GetString(original, "ZBITPIX");
        normalized.AddCard(new FitsCard("BITPIX", string.IsNullOrEmpty(zBitpix) ? "16" : zBitpix, "Original Data Depth", false));
        
        string zNaxis = GetString(original, "ZNAXIS");
        if (!string.IsNullOrEmpty(zNaxis))
        {
            normalized.AddCard(new FitsCard("NAXIS", zNaxis, "Original Axes", false));
            if (int.TryParse(zNaxis, out int dims))
            {
                for (int i = 1; i <= dims; i++)
                {
                    string val = GetString(original, $"ZNAXIS{i}");
                    normalized.AddCard(new FitsCard($"NAXIS{i}", val, $"Axis {i} Length", false));
                }
            }
        }
        else
        {
            normalized.AddCard(new FitsCard("NAXIS", "2", "Fallback Axes", false));
        }
        
        // 2. Copia Metadata (Clean)
        foreach (var card in original.Cards)
        {
            string key = card.Key.ToUpperInvariant().Trim();

            // Blacklist
            if (key == "SIMPLE" || key == "XTENSION" || key == "BITPIX" || key == "END" ||
                key.StartsWith("NAXIS") || 
                key.StartsWith("Z") ||     
                key.StartsWith("TFORM") || 
                key.StartsWith("TTYPE") || 
                key == "PCOUNT" || key == "GCOUNT" || key == "THEAP" || key == "TFIELDS" ||
                key == "DATAMIN" || key == "DATAMAX" || key == "DATAMEAN")
            {
                continue;
            }
            normalized.AddCard(new FitsCard(card.Key, card.Value, card.Comment, false));
        }

        // 3. Statistiche
        if (pixelData != null && pixelData.Length > 0)
        {
            try 
            {
                double min = double.MaxValue;
                double max = double.MinValue;
                double sum = 0;
                long count = 0;
                bool calculated = false;

                if (pixelData is float[,] f)
                {
                    count = f.Length;
                    foreach (float v in f) { if (v < min) min = v; if (v > max) max = v; sum += v; }
                    calculated = true;
                }
                else if (pixelData is int[,] i)
                {
                    count = i.Length;
                    foreach (int v in i) { if (v < min) min = v; if (v > max) max = v; sum += v; }
                    calculated = true;
                }
                else if (pixelData is short[,] s)
                {
                    count = s.Length;
                    foreach (short v in s) { if (v < min) min = v; if (v > max) max = v; sum += v; }
                    calculated = true;
                }
                else if (pixelData is double[,] d)
                {
                    count = d.Length;
                    foreach (double v in d) { if (v < min) min = v; if (v > max) max = v; sum += v; }
                    calculated = true;
                }

                if (calculated && count > 0)
                {
                    var culture = System.Globalization.CultureInfo.InvariantCulture;
                    normalized.AddCard(new FitsCard("DATAMIN", min.ToString("0.0000", culture), "Min Value", false));
                    normalized.AddCard(new FitsCard("DATAMAX", max.ToString("0.0000", culture), "Max Value", false));
                    normalized.AddCard(new FitsCard("DATAMEAN", (sum / count).ToString("0.0000", culture), "Mean Value", false));
                }
            }
            catch { /* Ignora errori statistiche */ }
        }

        // 4. Note Storiche (Unificate)
        string originalAlgo = GetString(original, "ZCMPTYPE");
        string historyMsg = "KomaLab - Decompressed by KomaLab";
        if (!string.IsNullOrEmpty(originalAlgo)) historyMsg += $" (Original Algo: {originalAlgo})";
        
        normalized.AddCard(new FitsCard("HISTORY", historyMsg, null, false));

        // END
        normalized.AddCard(new FitsCard("END", "", "", false));

        return normalized;
    }

    private string GetString(FitsHeader h, string key) => h.Cards.FirstOrDefault(x => x.Key == key)?.Value?.Trim()?.ToUpperInvariant() ?? "";
    private bool GetBool(FitsHeader h, string key) => h.Cards.FirstOrDefault(x => x.Key == key)?.Value?.Trim().ToUpperInvariant() == "T";

    // --- WRITE & UTILS ---

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
            for (int x = 0; x < w; x++) { T temp = matrix[y, x]; matrix[y, x] = matrix[mirrorY, x]; matrix[mirrorY, x] = temp; }
        }
    }
}