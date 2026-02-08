using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using KomaLab.Infrastructure;
using KomaLab.Models.Fits;
using KomaLab.Models.Fits.Structure;
using KomaLab.Models.Export;
using KomaLab.Services.Fits.Metadata;

namespace KomaLab.Services.Fits.IO
{
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

        public async Task<List<FitsHdu>> ReadAllHdusAsync(string path) => await Task.Run(() =>
        {
            try
            {
                Debug.WriteLine($"[FitsIoService] Apertura file: {path}");
                using var rawStream = _streamProvider.Open(path);
                using var stream = GetSmartStream(rawStream);
                return ReadAllHdus(stream);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FITS IO] ERRORE APERTURA {path}: {ex.Message}");
                return new List<FitsHdu>();
            }
        });

        public async Task<FitsHeader?> ReadHeaderAsync(string path) => await Task.Run(() =>
        {
            try
            {
                using var rawStream = _streamProvider.Open(path);
                using var stream = GetSmartStream(rawStream);
                var hdus = ReadAllHdus(stream);
                var selected = hdus.FirstOrDefault(h => !h.IsEmpty) ?? hdus.FirstOrDefault();
                return selected?.Header;
            }
            catch { return null; }
        });

        public async Task<Array?> ReadPixelDataAsync(string path) => await Task.Run(() =>
        {
            try
            {
                using var rawStream = _streamProvider.Open(path);
                using var stream = GetSmartStream(rawStream);
                var hdus = ReadAllHdus(stream);
                return hdus.FirstOrDefault(h => !h.IsEmpty)?.PixelData;
            }
            catch { return null; }
        });

        private Stream GetSmartStream(Stream originalStream)
        {
            if (!originalStream.CanSeek) return originalStream;
            try
            {
                byte[] header = new byte[2];
                int read = originalStream.Read(header, 0, 2);
                originalStream.Seek(0, SeekOrigin.Begin);

                if (read == 2 && header[0] == 0x1F && header[1] == 0x8B)
                {
                    Debug.WriteLine("[FitsIoService] Rilevato GZIP (File Level).");
                    return new GZipStream(originalStream, CompressionMode.Decompress);
                }
            }
            catch { originalStream.Seek(0, SeekOrigin.Begin); }
            return originalStream;
        }

        private List<FitsHdu> ReadAllHdus(Stream s)
        {
            var list = new List<FitsHdu>();
            FitsHeader? primaryHeader = null;
            int hduCount = 0;

            while (s.Position < s.Length)
            {
                var header = _reader.ReadHeader(s);
                if (header == null) break;

                long dataStartPos = s.Position;
                long dataBlockLen = _reader.GetDataBlockLength(header);
                long expectedNextHduPos = dataStartPos + dataBlockLen;

                int naxis = GetInt(header, "NAXIS", 0);
                
                // Gestione Header Primario
                if (hduCount == 0)
                {
                    primaryHeader = header;
                    // Se il primario è vuoto (tipico dei file compressi), lo salviamo e passiamo oltre
                    if (naxis == 0)
                    {
                        list.Add(new FitsHdu(header, Array.CreateInstance(typeof(byte), 0, 0), true));
                        ForceSeek(s, expectedNextHduPos);
                        hduCount++;
                        continue;
                    }
                }
                else if (primaryHeader != null)
                {
                    // Unisce i metadati del primario (es. TELESCOP) all'estensione corrente
                    MergePrimaryMetadata(header, primaryHeader);
                }

                // Identificazione Tipo Estensione
                string xtension = GetString(header, "XTENSION");
                bool zImageBool = GetBool(header, "ZIMAGE");
                string zTension = GetString(header, "ZTENSION");
                string zCmpType = GetString(header, "ZCMPTYPE");

                // È un'immagine compressa (fpack)?
                bool isCompressedImage = (xtension.Contains("BINTABLE") && (zImageBool || zTension == "IMAGE" || !string.IsNullOrEmpty(zCmpType)));
                // È una tabella vera e propria?
                bool isTable = xtension.Contains("TABLE") && !xtension.Contains("BINTABLE");
                // È una tabella binaria standard (non immagine)?
                bool isNormalBintable = xtension.Contains("BINTABLE") && !isCompressedImage;

                if (isCompressedImage)
                {
                    // 1. Decomprimi
                    var pixelData = FitsDecompression.DecompressImage(s, header, _reader);
                    
                    // 2. Normalizza Header (ZBITPIX -> BITPIX)
                    var normalizedHeader = NormalizeCompressedHeader(header);

                    // 3. Flip Verticale (Standard FITS)
                    if (pixelData != null && pixelData.Length > 0) FlipArrayVertical(pixelData);
                    
                    list.Add(new FitsHdu(normalizedHeader, pixelData, false));

                    ForceSeek(s, expectedNextHduPos);
                }
                else if (isTable || isNormalBintable)
                {
                    // Saltiamo le tabelle per ora (non supportate dalla visualizzazione immagine)
                    ForceSeek(s, expectedNextHduPos);
                }
                else
                {
                    // Immagine Standard non compressa
                    var data = _reader.ReadMatrix(s, header);
                    if (data != null && data.Length > 0) FlipArrayVertical(data);

                    var finalHeader = (hduCount == 0) ? header : PromoteToPrimaryHeader(header);
                    bool isEmpty = data == null || data.Length == 0;
                    
                    list.Add(new FitsHdu(finalHeader, data ?? Array.CreateInstance(typeof(byte), 0, 0), isEmpty));
                    ForceSeek(s, expectedNextHduPos);
                }
                hduCount++;
            }
            return list;
        }

        private void ForceSeek(Stream s, long targetPos)
        {
            if (!s.CanSeek) return;
            // Pad to 2880 bytes block alignment if necessary
            if (targetPos % 2880 != 0) targetPos += (2880 - (targetPos % 2880));
            
            if (s.Position != targetPos) s.Position = targetPos;
        }

        // =======================================================================
        // METADATA MERGING & NORMALIZATION
        // =======================================================================

        private void MergePrimaryMetadata(FitsHeader target, FitsHeader source)
        {
            var blockedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "SIMPLE", "XTENSION", "BITPIX", "NAXIS", "EXTEND", "PCOUNT", "GCOUNT", "TFIELDS", "THEAP", "END",
                "BSCALE", "BZERO", "BUNIT", "BLANK", "DATAMAX", "DATAMIN",
                "ZIMAGE", "ZCMPTYPE", "ZBITPIX", "ZNAXIS", "ZTILE1", "ZTILE2", "ZQUANTIZ", "ZBLANK",
                "CHECKSUM", "DATASUM"
            };

            foreach (var card in source.Cards)
            {
                string key = card.Key?.Trim().ToUpperInvariant() ?? "";

                if (string.IsNullOrEmpty(key)) continue;
                if (blockedKeys.Contains(key)) continue;
                if (key.StartsWith("NAXIS")) continue;
                if (key.StartsWith("Z")) continue;
                if (key.StartsWith("T") && key.Length > 1 && char.IsDigit(key[key.Length - 1])) continue;

                if (!target.Cards.Any(c => c.Key.Equals(key, StringComparison.OrdinalIgnoreCase)))
                {
                    target.AddCard(new FitsCard(card.Key, card.Value, card.Comment, false));
                }
            }
        }

        private FitsHeader NormalizeCompressedHeader(FitsHeader original)
        {
            var normalized = new FitsHeader();

            normalized.AddCard(new FitsCard("SIMPLE", "T", "Converted from Compressed FITS", false));
            normalized.AddCard(new FitsCard("BITPIX", GetString(original, "ZBITPIX"), "Original Depth", false));

            int dims = GetInt(original, "ZNAXIS", 0);
            normalized.AddCard(new FitsCard("NAXIS", dims.ToString(), "Original Axes", false));
            for (int i = 1; i <= dims; i++)
                normalized.AddCard(new FitsCard($"NAXIS{i}", GetString(original, $"ZNAXIS{i}"), $"Axis {i}", false));

            foreach (var card in original.Cards)
            {
                string key = card.Key.ToUpperInvariant().Trim();

                if (key == "SIMPLE" || key == "XTENSION" || key == "BITPIX" || key == "END" ||
                    key.StartsWith("NAXIS") || key.StartsWith("Z") || key.StartsWith("T") ||
                    key == "PCOUNT" || key == "GCOUNT" || key == "THEAP" || key == "EXTEND") continue;

                normalized.AddCard(new FitsCard(card.Key, card.Value, card.Comment, false));
            }
            normalized.AddCard(new FitsCard("END", "", "", false));
            return normalized;
        }

        // =======================================================================
        // SCRITTURA (WRITE)
        // =======================================================================

        public async Task WriteFileAsync(string path, Array data, FitsHeader header) 
            => await WriteFileAsync(path, data, header, FitsCompressionMode.None);

        public async Task WriteFileAsync(string path, Array data, FitsHeader header, FitsCompressionMode mode) => await Task.Run(() =>
        {
            // [FIX]: Sanitizzazione Header
            EnsureHeaderCompliance(header, data);
            
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
                    WriteDummyPrimaryHeader(fs);
                    var body = FitsCompression.CompressImage(data, header, mode, out var cHeader);
                    RawWriteHeader(fs, cHeader);
                    fs.Write(body);
                    PadStream(fs);
                }
            }
            finally
            {
                FlipArrayVertical(data); 
            }
        });

        public async Task WriteMergedFileAsync(string path, List<(Array Pixels, FitsHeader Header)> blocks, FitsCompressionMode mode) => await Task.Run(() =>
        {
            if (blocks == null || blocks.Count == 0) return;
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);

            if (mode != FitsCompressionMode.None) WriteDummyPrimaryHeader(fs);

            for (int i = 0; i < blocks.Count; i++)
            {
                var (pixels, header) = blocks[i];
                bool hasData = pixels != null && pixels.Length > 0;

                // [FIX]: Sanitizzazione Header
                if (hasData) EnsureHeaderCompliance(header, pixels);

                if (hasData) FlipArrayVertical(pixels);
                try
                {
                    if (mode != FitsCompressionMode.None && hasData)
                    {
                        var body = FitsCompression.CompressImage(pixels!, header, mode, out var cHeader);
                        RawWriteHeader(fs, cHeader);
                        fs.Write(body);
                        PadStream(fs);
                    }
                    else
                    {
                        if (i == 0 && mode == FitsCompressionMode.None) _writer.WriteHeader(fs, header);
                        else _writer.WriteImageExtension(fs, header, pixels!);

                        if (hasData && i == 0 && mode == FitsCompressionMode.None) _writer.WriteMatrix(fs, pixels!);
                    }
                }
                finally
                {
                    if (hasData) FlipArrayVertical(pixels!);
                }
            }
        });

        // =======================================================================
        // HELPER PER LA COMPLIANCE FITS (CORRETTO)
        // =======================================================================

        private void EnsureHeaderCompliance(FitsHeader header, Array data)
        {
            if (data == null) return;
            Type t = data.GetType().GetElementType();

            // Usiamo i metodi di FitsHeader per manipolare le card
            header.RemoveCard("BSCALE");
            header.RemoveCard("BZERO");
            header.RemoveCard("BITPIX");

            if (t == typeof(ushort))
            {
                header.AddCard(new FitsCard("BITPIX", "16", "16-bit data", false));
                header.AddCard(new FitsCard("BSCALE", "1.0", "default scaling factor", false));
                header.AddCard(new FitsCard("BZERO", "32768.0", "offset data range to that of unsigned short", false));
            }
            else if (t == typeof(uint))
            {
                header.AddCard(new FitsCard("BITPIX", "32", "32-bit data", false));
                header.AddCard(new FitsCard("BSCALE", "1.0", "default scaling factor", false));
                header.AddCard(new FitsCard("BZERO", "2147483648.0", "offset data range to that of unsigned int", false));
            }
            else if (t == typeof(byte))
            {
                header.AddCard(new FitsCard("BITPIX", "8", "8-bit data", false));
            }
            else if (t == typeof(short))
            {
                header.AddCard(new FitsCard("BITPIX", "16", "16-bit data", false));
            }
            else if (t == typeof(int))
            {
                header.AddCard(new FitsCard("BITPIX", "32", "32-bit data", false));
            }
            else if (t == typeof(float))
            {
                header.AddCard(new FitsCard("BITPIX", "-32", "32-bit floating point", false));
            }
            else if (t == typeof(double))
            {
                header.AddCard(new FitsCard("BITPIX", "-64", "64-bit floating point", false));
            }
            
            // Gestione END: rimuoviamo se esiste e riaggiungiamo alla fine
            // Assumiamo che RemoveCard cancelli per chiave
            if (header.Cards.Any(c => c.Key == "END"))
            {
                header.RemoveCard("END");
            }
            header.AddCard(new FitsCard("END", "", "", false));
        }

        // =======================================================================
        // UTILS
        // =======================================================================

        private void WriteDummyPrimaryHeader(Stream s)
        {
            var h = new FitsHeader();
            h.AddCard(new FitsCard("SIMPLE", "T", "", false));
            h.AddCard(new FitsCard("BITPIX", "8", "", false));
            h.AddCard(new FitsCard("NAXIS", "0", "", false));
            h.AddCard(new FitsCard("EXTEND", "T", "", false));
            RawWriteHeader(s, h);
        }

        private void RawWriteHeader(Stream stream, FitsHeader header)
        {
            int bytesWritten = 0;
            foreach (var card in header.Cards)
            {
                if (card.Key == "END") break;
                stream.Write(Encoding.ASCII.GetBytes(FitsFormatting.PadTo80(card)), 0, 80);
                bytesWritten += 80;
            }
            stream.Write(Encoding.ASCII.GetBytes("END".PadRight(80)), 0, 80);
            bytesWritten += 80;

            int rem = bytesWritten % 2880;
            if (rem > 0) stream.Write(Enumerable.Repeat((byte)' ', 2880 - rem).ToArray(), 0, 2880 - rem);
        }

        private void PadStream(Stream s)
        {
            long rem = s.Position % 2880;
            if (rem > 0) s.Write(new byte[2880 - rem], 0, (int)(2880 - rem));
        }

        private FitsHeader PromoteToPrimaryHeader(FitsHeader original)
        {
            var p = new FitsHeader();
            p.AddCard(new FitsCard("SIMPLE", "T", "", false));
            foreach (var c in original.Cards)
            {
                if (c.Key != "SIMPLE" && c.Key != "XTENSION" && c.Key != "END") p.AddCard(c);
            }
            p.AddCard(new FitsCard("END", "", "", false));
            return p;
        }

        private string GetString(FitsHeader h, string key) => h.Cards.FirstOrDefault(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase))?.Value?.Trim()?.ToUpperInvariant()?.Replace("'", "") ?? "";
        private int GetInt(FitsHeader h, string k, int def) => int.TryParse(GetString(h, k), out int v) ? v : def;
        private double GetDouble(FitsHeader h, string k, double def) => double.TryParse(GetString(h, k), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : def;
        private bool GetBool(FitsHeader h, string key) => GetString(h, key) == "T";

        private void FlipArrayVertical(Array matrix)
        {
            if (matrix == null || matrix.Length == 0) return;
            int h = matrix.GetLength(0);
            int w = matrix.GetLength(1);
            int halfH = h / 2;

            if (matrix is byte[,] b) { for (int y = 0; y < halfH; y++) for (int x = 0; x < w; x++) (b[y, x], b[h - 1 - y, x]) = (b[h - 1 - y, x], b[y, x]); }
            else if (matrix is short[,] s) { for (int y = 0; y < halfH; y++) for (int x = 0; x < w; x++) (s[y, x], s[h - 1 - y, x]) = (s[h - 1 - y, x], s[y, x]); }
            else if (matrix is ushort[,] us) { for (int y = 0; y < halfH; y++) for (int x = 0; x < w; x++) (us[y, x], us[h - 1 - y, x]) = (us[h - 1 - y, x], us[y, x]); }
            else if (matrix is int[,] i) { for (int y = 0; y < halfH; y++) for (int x = 0; x < w; x++) (i[y, x], i[h - 1 - y, x]) = (i[h - 1 - y, x], i[y, x]); }
            else if (matrix is uint[,] ui) { for (int y = 0; y < halfH; y++) for (int x = 0; x < w; x++) (ui[y, x], ui[h - 1 - y, x]) = (ui[h - 1 - y, x], ui[y, x]); }
            else if (matrix is float[,] f) { for (int y = 0; y < halfH; y++) for (int x = 0; x < w; x++) (f[y, x], f[h - 1 - y, x]) = (f[h - 1 - y, x], f[y, x]); }
            else if (matrix is double[,] d) { for (int y = 0; y < halfH; y++) for (int x = 0; x < w; x++) (d[y, x], d[h - 1 - y, x]) = (d[h - 1 - y, x], d[y, x]); }
        }

        public async Task CopyFileAsync(string source, string dest) => await Task.Run(() => File.Copy(source, dest, true));
        public string BuildRawPath(string sub, string name)
        {
            string p = Path.Combine(Path.GetTempPath(), "KomaLab", sub);
            Directory.CreateDirectory(p);
            return Path.Combine(p, name);
        }
        public void TryDeleteFile(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }
    }
}