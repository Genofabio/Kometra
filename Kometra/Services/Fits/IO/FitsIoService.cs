using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using Kometra.Models.Fits;
using Kometra.Infrastructure;
using Kometra.Models.Export;
using Kometra.Models.Fits.Structure;
using Kometra.Services.Fits.Metadata;

namespace Kometra.Services.Fits.IO
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
                // Usiamo GetSmartStream che gestisce GZIP e rende lo stream Seekable
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
                // Leggiamo solo il primo header valido
                return _reader.ReadHeader(stream);
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

        /// <summary>
        /// Gestisce la decompressione GZIP trasparente e garantisce che lo stream ritornato sia SEEKABLE.
        /// FITS richiede seek per la decompressione Tile e per saltare i blocchi.
        /// </summary>
        private Stream GetSmartStream(Stream originalStream)
        {
            if (!originalStream.CanSeek)
            {
                // Se lo stream originale non è seekable, copiamo tutto in memoria subito
                var ms = new MemoryStream();
                originalStream.CopyTo(ms);
                ms.Position = 0;
                return GetSmartStream(ms); // Ricorsivo per controllare se il contenuto è GZIP
            }

            try
            {
                byte[] header = new byte[2];
                int read = originalStream.Read(header, 0, 2);
                originalStream.Seek(0, SeekOrigin.Begin); // Reset posizione

                // Controllo Magic Number GZIP (0x1F 0x8B)
                if (read == 2 && header[0] == 0x1F && header[1] == 0x8B)
                {
                    Debug.WriteLine("[FitsIoService] Rilevato GZIP. Decompressione in memoria per garantire Seek...");
                    
                    // GZipStream non supporta Seek/Length. Decomprimiamo in MemoryStream.
                    var decompressedStream = new MemoryStream();
                    using (var gzip = new GZipStream(originalStream, CompressionMode.Decompress, leaveOpen: true))
                    {
                        gzip.CopyTo(decompressedStream);
                    }
                    decompressedStream.Position = 0;
                    return decompressedStream;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FitsIoService] Errore rilevamento GZIP: {ex.Message}");
                originalStream.Seek(0, SeekOrigin.Begin);
            }

            return originalStream;
        }

        private List<FitsHdu> ReadAllHdus(Stream s)
        {
            var list = new List<FitsHdu>();
            FitsHeader? primaryHeader = null;
            int hduCount = 0;

            // Loop robusto che non dipende solo da s.Length
            while (true)
            {
                // Controllo sicurezza EOF
                if (s.CanSeek && s.Position >= s.Length) break;

                var header = _reader.ReadHeader(s);
                if (header == null) break; // Fine file corretta

                long dataStartPos = s.Position;
                long dataBlockLen = _reader.GetDataBlockLength(header);
                long expectedNextHduPos = dataStartPos + dataBlockLen;

                int naxis = GetInt(header, "NAXIS", 0);
                
                // Gestione Header Primario
                if (hduCount == 0)
                {
                    primaryHeader = header;
                    // Se il primario è vuoto (tipico dei file compressi o MEF), lo salviamo e passiamo oltre
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
                    MergePrimaryMetadata(header, primaryHeader);
                }

                // Identificazione Tipo Estensione
                string xtension = GetString(header, "XTENSION");
                bool zImageBool = GetBool(header, "ZIMAGE");
                string zTension = GetString(header, "ZTENSION");
                string zCmpType = GetString(header, "ZCMPTYPE");

                bool isCompressedImage = (xtension.Contains("BINTABLE") && (zImageBool || zTension == "IMAGE" || !string.IsNullOrEmpty(zCmpType)));
                bool isTable = xtension.Contains("TABLE") && !xtension.Contains("BINTABLE");
                bool isNormalBintable = xtension.Contains("BINTABLE") && !isCompressedImage;

                if (isCompressedImage)
                {
                    var pixelData = FitsDecompression.DecompressImage(s, header, _reader);
                    var normalizedHeader = NormalizeCompressedHeader(header);
                    
                    pixelData = ExpandSpectrumToSquare(pixelData, normalizedHeader);

                    if (pixelData != null && pixelData.Length > 0) FlipArrayVertical(pixelData);
                    list.Add(new FitsHdu(normalizedHeader, pixelData, false));

                    ForceSeek(s, expectedNextHduPos);
                }
                else if (isTable || isNormalBintable)
                {
                    // Saltiamo le tabelle generiche
                    ForceSeek(s, expectedNextHduPos);
                }
                else
                {
                    // Immagine Standard
                    var data = _reader.ReadMatrix(s, header);
                    var finalHeader = (hduCount == 0) ? header : PromoteToPrimaryHeader(header);
                    
                    data = ExpandSpectrumToSquare(data, finalHeader);
                    if (data != null && data.Length > 0) FlipArrayVertical(data);
                    
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
            if (targetPos % 2880 != 0) targetPos += (2880 - (targetPos % 2880));
            
            if (s.CanSeek)
            {
                if (targetPos > s.Length) s.Position = s.Length;
                else s.Position = targetPos;
            }
            else
            {
                long bytesToSkip = targetPos - s.Position;
                if (bytesToSkip > 0)
                {
                    byte[] junk = new byte[Math.Min(bytesToSkip, 4096)];
                    while (bytesToSkip > 0)
                    {
                        int read = s.Read(junk, 0, (int)Math.Min(bytesToSkip, junk.Length));
                        if (read == 0) break;
                        bytesToSkip -= read;
                    }
                }
            }
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
                if (string.IsNullOrEmpty(key) || blockedKeys.Contains(key)) continue;
                if (key.StartsWith("NAXIS") || key.StartsWith("Z")) continue;
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

            string zBzero = GetString(original, "ZBZERO");
            string zBscale = GetString(original, "ZBSCALE");
            if (!string.IsNullOrEmpty(zBscale)) normalized.AddCard(new FitsCard("BSCALE", zBscale, "Scaling factor", false));
            if (!string.IsNullOrEmpty(zBzero)) normalized.AddCard(new FitsCard("BZERO", zBzero, "Zero point", false));

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
            finally { FlipArrayVertical(data); }
        });

        public async Task WriteMergedFileAsync(string path, List<(Array Pixels, FitsHeader Header)> blocks, FitsCompressionMode mode) => await Task.Run(() =>
        {
            if (blocks == null || blocks.Count == 0) return;
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);

            // 1. SCRITTURA PRIMARIO (HDU 0)
            // Prendiamo l'header del primo blocco per popolare i metadati globali (Telescopio, Oggetto, ecc.)
            var masterHeader = blocks[0].Header;
            WriteEnrichedPrimaryHeader(fs, masterHeader);

            // 2. SCRITTURA ESTENSIONI (HDU 1..N)
            int extensionCount = 0;
            for (int i = 0; i < blocks.Count; i++)
            {
                var (pixels, header) = blocks[i];
                
                // [FIX CRITICO] Saltiamo blocchi senza pixel per evitare estensioni "fantasma" vuote
                if (pixels == null || pixels.Length == 0) continue;

                extensionCount++;

                // Generazione nome estensione se mancante
                if (!header.Cards.Any(c => c.Key == "EXTNAME"))
                {
                    header.AddCard(new FitsCard("EXTNAME", $"FRAME_{extensionCount}", "Extension Name", false));
                }

                FlipArrayVertical(pixels);
                try
                {
                    if (mode != FitsCompressionMode.None)
                    {
                        var body = FitsCompression.CompressImage(pixels!, header, mode, out var cHeader);
                        RawWriteHeader(fs, cHeader);
                        fs.Write(body);
                        PadStream(fs);
                    }
                    else
                    {
                        EnsureHeaderCompliance(header, pixels);
                        _writer.WriteImageExtension(fs, header, pixels!);
                    }
                }
                finally
                {
                    FlipArrayVertical(pixels!);
                }
            }
        });
        
        private Array? ExpandSpectrumToSquare(Array? source, FitsHeader header)
        {
            if (source == null || source.Length == 0) return source;

            int height = source.GetLength(0); // Righe
            int width = source.GetLength(1);  // Colonne

            // Se l'immagine è già un'immagine 2D "vera" (es. altezza > 20), non la tocchiamo.
            // Usiamo 20 come soglia: difficilmente uno spettro ha più di 20 canali/ordini.
            if (height >= 5 || width <= 1) return source;

            int bandThickness = width / height; 
    if (bandThickness < 1) bandThickness = 1;
    int targetHeight = bandThickness * height;

    Type elementType = source.GetType().GetElementType()!;
    Array expanded = Array.CreateInstance(elementType, targetHeight, width);
    int bytesPerRow = Buffer.ByteLength(source) / height;

    // --- LOGICA DI NORMALIZZAZIONE (Opzionale ma utile per gli spettri) ---
    // Se è un array di float o double, controlliamo i valori e li normalizziamo
    // per evitare che valori come 1e-16 o NaN distruggano il visualizzatore.
    if (source is float[,] fSource)
    {
        float min = float.MaxValue;
        float max = float.MinValue;
        
        // 1. Troviamo il VERO minimo e massimo (ignorando i NaN)
        foreach (float val in fSource)
        {
            if (!float.IsNaN(val) && !float.IsInfinity(val))
            {
                if (val < min) min = val;
                if (val > max) max = val;
            }
        }
        
        Debug.WriteLine($"[Spettro Debug] Flusso Min: {min}, Max: {max}");

        // Se l'escursione è microscopica o ci sono valori anomali, forziamo una normalizzazione a 65535
        float range = max - min;
        if (range > 0 && (max < 0.1f || max > 1000000f)) 
        {
            Debug.WriteLine("[Spettro Debug] Valori microscopici/enormi rilevati. Normalizzo per la visualizzazione...");
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float val = fSource[y, x];
                    if (float.IsNaN(val) || float.IsInfinity(val)) fSource[y, x] = 0;
                    else fSource[y, x] = ((val - min) / range) * 65535.0f; // Stira tra 0 e 65535
                }
            }
        }
    }

    // --- COPIA ESPANSA (Schiaccia e spalma le righe) ---
    for (int originalY = 0; originalY < height; originalY++)
    {
        int srcOffset = originalY * bytesPerRow;
        for (int rep = 0; rep < bandThickness; rep++)
        {
            int destY = (originalY * bandThickness) + rep;
            int destOffset = destY * bytesPerRow;
            Buffer.BlockCopy(source, srcOffset, expanded, destOffset, bytesPerRow);
        }
    }

    // --- AGGIORNAMENTO HEADER ---
    header.RemoveCard("NAXIS");
    header.AddCard(new FitsCard("NAXIS", "2", "Forced to 2D for visualization", false));

    header.RemoveCard("NAXIS2");
    header.AddCard(new FitsCard("NAXIS2", targetHeight.ToString(), "Expanded multi-channel spectrum", false));
    
    // Rimuoviamo DATAMIN/DATAMAX originali perché ora sono sballati e potrebbero confondere l'ActiveRenderer
    header.RemoveCard("DATAMIN");
    header.RemoveCard("DATAMAX");

    return expanded;
}

        // =======================================================================
        // HELPER & UTILS
        // =======================================================================

        private void EnsureHeaderCompliance(FitsHeader header, Array data)
        {
            if (data == null) return;
            Type t = data.GetType().GetElementType();
            header.RemoveCard("BSCALE"); header.RemoveCard("BZERO"); header.RemoveCard("BITPIX");

            if (t == typeof(ushort)) { header.AddCard(new FitsCard("BITPIX", "16", "", false)); header.AddCard(new FitsCard("BSCALE", "1.0", "", false)); header.AddCard(new FitsCard("BZERO", "32768.0", "", false)); }
            else if (t == typeof(uint)) { header.AddCard(new FitsCard("BITPIX", "32", "", false)); header.AddCard(new FitsCard("BSCALE", "1.0", "", false)); header.AddCard(new FitsCard("BZERO", "2147483648.0", "", false)); }
            else if (t == typeof(byte)) header.AddCard(new FitsCard("BITPIX", "8", "", false));
            else if (t == typeof(short)) header.AddCard(new FitsCard("BITPIX", "16", "", false));
            else if (t == typeof(int)) header.AddCard(new FitsCard("BITPIX", "32", "", false));
            else if (t == typeof(float)) header.AddCard(new FitsCard("BITPIX", "-32", "", false));
            else if (t == typeof(double)) header.AddCard(new FitsCard("BITPIX", "-64", "", false));

            if (header.Cards.Any(c => c.Key == "END")) header.RemoveCard("END");
            header.AddCard(new FitsCard("END", "", "", false));
        }

        private void WriteDummyPrimaryHeader(Stream s)
        {
            var h = new FitsHeader();
            h.AddCard(new FitsCard("SIMPLE", "T", "", false));
            h.AddCard(new FitsCard("BITPIX", "8", "", false));
            h.AddCard(new FitsCard("NAXIS", "0", "", false));
            h.AddCard(new FitsCard("EXTEND", "T", "", false));
            RawWriteHeader(s, h);
        }

        private void WriteEnrichedPrimaryHeader(Stream s, FitsHeader sourceHeader)
        {
            var p = new FitsHeader();
            
            // Struttura fissa Primary MEF
            p.AddCard(new FitsCard("SIMPLE", "T", "Standard FITS format", false));
            p.AddCard(new FitsCard("BITPIX", "8", "No data in Primary HDU", false));
            p.AddCard(new FitsCard("NAXIS", "0", "No data in Primary HDU", false));
            p.AddCard(new FitsCard("EXTEND", "T", "Extensions are permitted", false));

            // Copia selettiva metadati
            var blocked = new HashSet<string> { 
                "SIMPLE", "XTENSION", "BITPIX", "NAXIS", "NAXIS1", "NAXIS2", "NAXIS3", 
                "PCOUNT", "GCOUNT", "GROUPS", "TFIELDS", "THEAP", "END", "EXTEND",
                "BSCALE", "BZERO", "CHECKSUM", "DATASUM", "EXTNAME"
            };

            foreach (var card in sourceHeader.Cards)
            {
                string key = card.Key?.ToUpperInvariant().Trim();
                if (string.IsNullOrEmpty(key)) continue;
                
                if (!blocked.Contains(key) && !p.Cards.Any(c => c.Key == key))
                {
                    p.AddCard(new FitsCard(card.Key, card.Value, card.Comment, false));
                }
            }
            p.AddCard(new FitsCard("COMMENT", "Kometra - MEF Container", "", false));
            RawWriteHeader(s, p);
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
            foreach (var c in original.Cards) if (c.Key != "SIMPLE" && c.Key != "XTENSION" && c.Key != "END") p.AddCard(c);
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
        public string BuildRawPath(string sub, string name) { string p = Path.Combine(Path.GetTempPath(), "Kometra", sub); Directory.CreateDirectory(p); return Path.Combine(p, name); }
        public void TryDeleteFile(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }
    }
}