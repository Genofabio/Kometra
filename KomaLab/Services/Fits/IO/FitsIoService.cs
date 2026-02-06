using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics; 
using System.Text;
using System.Threading.Tasks;
using KomaLab.Infrastructure;
using KomaLab.Models.Fits;
using KomaLab.Models.Fits.Structure;
using KomaLab.Models.Export;
using KomaLab.Services.Fits.Metadata; // Necessario per FitsCompressionMode

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
    // 1. LETTURA (READ)
    // =======================================================================

    public async Task<List<FitsHdu>> ReadAllHdusAsync(string path) => await Task.Run(() => {
        try 
        { 
            using var s = _streamProvider.Open(path);
            return ReadAllHdus(s);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FITS IO] ERRORE APERTURA {path}: {ex.Message}");
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
        FitsHeader? primaryHeader = null; 
        int hduCount = 0;
        
        while (s.Position < s.Length)
        {
            var header = _reader.ReadHeader(s);
            int naxis = GetInt(header, "NAXIS", 0);

            if (hduCount == 0) 
            {
                primaryHeader = header;
                if (naxis == 0)
                {
                    list.Add(new FitsHdu(header, Array.CreateInstance(typeof(byte), 0, 0), true));
                    hduCount++;
                    continue; 
                }
            }
            else if (primaryHeader != null) 
            {
                MergePrimaryMetadata(header, primaryHeader);
            }

            string xtension = GetString(header, "XTENSION");
            bool zImageBool = GetBool(header, "ZIMAGE");
            bool isCompressedImage = xtension.Contains("BINTABLE") && zImageBool;
            bool isBintable = xtension.Contains("BINTABLE") && !zImageBool;

            if (isCompressedImage)
            {
                var pixelData = FitsDecompression.DecompressImage(s, header, _reader);
                var normalizedHeader = NormalizeCompressedHeader(header, pixelData);
                if (pixelData != null && pixelData.Length > 0) FlipArrayVertical(pixelData);
                list.Add(new FitsHdu(normalizedHeader, pixelData, false));
            }
            else if (isBintable)
            {
                _reader.SkipDataBlock(s, header);
            }
            else
            {
                var data = _reader.ReadMatrix(s, header);
                if (data != null && data.Length > 0) FlipArrayVertical(data);
                
                var finalHeader = PromoteToPrimaryHeader(header);
                bool isEmpty = data == null || data.Length == 0;
                list.Add(new FitsHdu(finalHeader, data ?? Array.CreateInstance(typeof(byte), 0, 0), isEmpty));
            }
            hduCount++;
        }
        return list;
    }

    // =======================================================================
    // 2. SCRITTURA (WRITE) - Con supporto Compressione
    // =======================================================================

    public async Task WriteFileAsync(string path, Array data, FitsHeader header) 
        => await WriteFileAsync(path, data, header, FitsCompressionMode.None);

    public async Task WriteFileAsync(string path, Array data, FitsHeader header, FitsCompressionMode mode) => await Task.Run(() => {
        // RAM (Top-Down) -> FITS (Bottom-Up)
        FlipArrayVertical(data); 

        try 
        {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            
            if (mode == FitsCompressionMode.None)
            {
                _writer.WriteHeader(fs, header);
                _writer.WriteMatrix(fs, data); 
            }
            else
            {
                // Compressione Tile (Rice/Gzip)
                var body = FitsCompression.CompressImage(data, header, mode, out var cHeader);
                RawWriteHeader(fs, cHeader);
                fs.Write(body);
                PadStream(fs);
            }
        }
        finally 
        {
            // Restore per KomaLab RAM
            FlipArrayVertical(data);
        }
    });

    public async Task WriteMergedFileAsync(string path, List<(Array Pixels, FitsHeader Header)> blocks)
        => await WriteMergedFileAsync(path, blocks, FitsCompressionMode.None);

    public async Task WriteMergedFileAsync(string path, List<(Array Pixels, FitsHeader Header)> blocks, FitsCompressionMode mode) => await Task.Run(() => {
        if (blocks == null || blocks.Count == 0) return;

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        
        for (int i = 0; i < blocks.Count; i++)
        {
            var (pixels, header) = blocks[i];
            bool hasData = pixels != null && pixels.Length > 0;
            
            if (hasData) FlipArrayVertical(pixels);

            try 
            {
                // HDU 0 (Primary) è solitamente Null (NAXIS=0) in un merge batch e non va compresso
                if (mode != FitsCompressionMode.None && hasData)
                {
                    var body = FitsCompression.CompressImage(pixels!, header, mode, out var cHeader);
                    
                    // Se è il primo HDU ma ha dati (raro in MEF batch ma possibile)
                    if (i == 0) RawWriteHeader(fs, cHeader);
                    else RawWriteHeader(fs, cHeader); // Le Z-Keywords gestiscono già XTENSION=BINTABLE

                    fs.Write(body);
                    PadStream(fs);
                }
                else
                {
                    // Scrittura Standard
                    if (i == 0) _writer.WriteHeader(fs, header);
                    else _writer.WriteImageExtension(fs, header, pixels!);

                    if (hasData && i == 0) _writer.WriteMatrix(fs, pixels!);
                }
            }
            finally 
            {
                if (hasData) FlipArrayVertical(pixels!);
            }
        }
    });

    // =======================================================================
    // 3. UTILS E HELPERS DI BASSO LIVELLO
    // =======================================================================

    /// <summary>
    /// Scrive l'header esattamente come fornito, senza promozioni automatiche.
    /// Indispensabile per mantenere XTENSION='BINTABLE' nei file compressi.
    /// </summary>
    private void RawWriteHeader(Stream stream, FitsHeader header)
    {
        int bytesWritten = 0;
        foreach (var card in header.Cards)
        {
            if (card.Key == "END") break;
            string line = FitsFormatting.PadTo80(card);
            byte[] bytes = Encoding.ASCII.GetBytes(line);
            stream.Write(bytes, 0, 80);
            bytesWritten += 80;
        }

        byte[] endLine = Encoding.ASCII.GetBytes("END".PadRight(80));
        stream.Write(endLine, 0, 80);
        bytesWritten += 80;

        // Padding Header (Spazi)
        int remainder = bytesWritten % 2880;
        if (remainder > 0)
        {
            int needed = 2880 - remainder;
            byte[] padding = Enumerable.Repeat((byte)' ', needed).ToArray();
            stream.Write(padding, 0, padding.Length);
        }
    }

    private void PadStream(Stream s)
    {
        long remainder = s.Position % 2880;
        if (remainder > 0)
        {
            int needed = (int)(2880 - remainder);
            s.Write(new byte[needed], 0, needed);
        }
    }

    private FitsHeader PromoteToPrimaryHeader(FitsHeader original)
    {
        var promoted = new FitsHeader();
        promoted.AddCard(new FitsCard("SIMPLE", "T", "Standard FITS Image", false));
        foreach (var card in original.Cards) {
            string key = card.Key.ToUpperInvariant().Trim();
            if (key == "SIMPLE" || key == "XTENSION" || key == "END") continue;
            promoted.AddCard(new FitsCard(card.Key, card.Value, card.Comment, false));
        }
        promoted.AddCard(new FitsCard("END", "", "", false));
        return promoted;
    }

    private void MergePrimaryMetadata(FitsHeader target, FitsHeader source)
    {
        var structuralKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) 
        { "SIMPLE", "XTENSION", "BITPIX", "NAXIS", "PCOUNT", "GCOUNT", "EXTNAME", "END", "BSCALE", "BZERO" };

        foreach (var card in source.Cards) {
            string key = card.Key?.ToUpperInvariant().Trim() ?? "";
            if (string.IsNullOrEmpty(key) || structuralKeys.Contains(key) || key.StartsWith("NAXIS")) continue;
            if (!target.Cards.Any(c => c.Key.Equals(key, StringComparison.OrdinalIgnoreCase)))
                target.AddCard(new FitsCard(card.Key, card.Value, card.Comment, false));
        }
    }

    private FitsHeader NormalizeCompressedHeader(FitsHeader original, Array? pixelData)
    {
        var normalized = new FitsHeader();
        normalized.AddCard(new FitsCard("SIMPLE", "T", "Converted from Compressed FITS", false));
        normalized.AddCard(new FitsCard("BITPIX", GetString(original, "ZBITPIX"), "Original Data Depth", false));
        
        int dims = GetInt(original, "ZNAXIS", 0);
        normalized.AddCard(new FitsCard("NAXIS", dims.ToString(), "Original Axes", false));
        for (int i = 1; i <= dims; i++)
            normalized.AddCard(new FitsCard($"NAXIS{i}", GetString(original, $"ZNAXIS{i}"), $"Axis {i} Length", false));

        foreach (var card in original.Cards) {
            string key = card.Key.ToUpperInvariant().Trim();
            if (key == "SIMPLE" || key == "XTENSION" || key.StartsWith("NAXIS") || key.StartsWith("Z") || key == "END") continue;
            normalized.AddCard(new FitsCard(card.Key, card.Value, card.Comment, false));
        }
        normalized.AddCard(new FitsCard("END", "", "", false));
        return normalized;
    }

    private string GetString(FitsHeader h, string key) => h.Cards.FirstOrDefault(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase))?.Value?.Trim()?.ToUpperInvariant() ?? "";
    private int GetInt(FitsHeader h, string k, int def) => int.TryParse(GetString(h, k), out int v) ? v : def;
    private bool GetBool(FitsHeader h, string key) => GetString(h, key) == "T";

    private void FlipArrayVertical(Array matrix) {
        if (matrix == null || matrix.Length == 0) return;
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
                T temp = matrix[y, x]; matrix[y, x] = matrix[mirrorY, x]; matrix[mirrorY, x] = temp; 
            }
        }
    }

    public async Task CopyFileAsync(string source, string dest) => await Task.Run(() => File.Copy(source, dest, true));
    public string BuildRawPath(string sub, string name) {
        string p = Path.Combine(Path.GetTempPath(), "KomaLab", sub);
        Directory.CreateDirectory(p); return Path.Combine(p, name);
    }
    public void TryDeleteFile(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }
}