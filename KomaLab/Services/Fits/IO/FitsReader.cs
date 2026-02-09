using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Diagnostics; // Fondamentale per i print
using System.Buffers.Binary;
using KomaLab.Models.Fits.Structure;
using KomaLab.Services.Fits.Metadata;

namespace KomaLab.Services.Fits.IO;

public class FitsReader
{
    private const int BlockSize = 2880;
    private const int CardSize = 80;
    private const int CardsPerBlock = 36;

    /// <summary>
    /// Legge un Header FITS dallo stream.
    /// Ritorna NULL se raggiunge la fine del file o incontra solo padding vuoto.
    /// </summary>
    public FitsHeader? ReadHeader(Stream stream)
    {
        var header = new FitsHeader();
        byte[] buffer = new byte[BlockSize];
        bool endFound = false;
        bool isFirstBlock = true;
        int validCardsFound = 0;
        int blockCount = 0;

        long startPos = stream.CanSeek ? stream.Position : -1;
        Debug.WriteLine($"[FitsReader] --- INIZIO READ HEADER (Pos: {startPos}) ---");

        while (!endFound)
        {
            blockCount++;
            
            // 1. Tenta di leggere un blocco
            int bytesRead = TryReadBlock(stream, buffer);

            // CASO A: Fine file pulita
            if (bytesRead == 0)
            {
                if (isFirstBlock)
                {
                    Debug.WriteLine("[FitsReader] EOF raggiunto all'inizio di un nuovo header. Nessun altro HDU.");
                    return null; 
                }
                Debug.WriteLine("[FitsReader] ERRORE: Fine file improvvisa durante la lettura dell'header!");
                throw new EndOfStreamException("Fine del file inaspettata: Header incompleto senza keyword END.");
            }

            // CASO B: File tronco o blocco parziale
            if (bytesRead < BlockSize)
            {
                Debug.WriteLine($"[FitsReader] WARNING: Blocco parziale ({bytesRead}/{BlockSize} bytes).");

                // Se è il primo blocco e contiene solo zeri/spazi, è spazzatura finale -> Ignora.
                if (isFirstBlock && IsJunkBlock(buffer, bytesRead)) 
                {
                    Debug.WriteLine("[FitsReader] Il blocco parziale è solo padding/junk. Ignoro e chiudo.");
                    return null;
                }

                Debug.WriteLine("[FitsReader] Applico padding automatico per completare il blocco.");
                Array.Fill(buffer, (byte)' ', bytesRead, BlockSize - bytesRead);
            }

            // 2. Parsing delle Card
            for (int i = 0; i < CardsPerBlock; i++)
            {
                int offset = i * CardSize;
                string line = Encoding.ASCII.GetString(buffer, offset, CardSize);

                // Controllo keyword END
                if (line.StartsWith("END")) 
                {
                    if (line.Length >= 8 && string.IsNullOrWhiteSpace(line.Substring(3)))
                    {
                        Debug.WriteLine($"[FitsReader] Trovata keyword END nel blocco {blockCount}, card {i}.");
                        endFound = true;
                        header.AddCard(new FitsCard("END", string.Empty, string.Empty, true));
                        break; 
                    }
                }

                var card = FitsFormatting.ParseLine(line);
                
                // Debug delle keyword critiche per capire cosa stiamo leggendo
                if (card.Key == "SIMPLE" || card.Key == "XTENSION" || card.Key == "BITPIX" || card.Key.StartsWith("NAXIS") || card.Key == "PCOUNT" || card.Key == "GCOUNT")
                {
                    Debug.WriteLine($"[FitsReader] Chiave Trovata: {card.Key} = {card.Value}");
                }

                if (string.IsNullOrWhiteSpace(card.Key) && string.IsNullOrWhiteSpace(card.Value) && string.IsNullOrWhiteSpace(card.Comment)) 
                    continue;

                header.AddCard(card);
                validCardsFound++;
            }

            // CASO C: Blocco di zeri valido apparentemente ma senza contenuto
            if (isFirstBlock && validCardsFound == 0 && !endFound)
            {
                Debug.WriteLine("[FitsReader] Blocco letto ma nessuna card valida trovata. Ritorno NULL.");
                return null;
            }

            isFirstBlock = false;
        }

        Debug.WriteLine($"[FitsReader] Header completato. Totale Card: {header.Cards.Count}");
        return header;
    }

    public Array ReadMatrix(Stream stream, FitsHeader header)
    {
        long pos = stream.CanSeek ? stream.Position : -1;
        Debug.WriteLine($"[FitsReader] --- INIZIO READ MATRIX (Pos: {pos}) ---");

        int bitpix = GetInternalInt(header, "BITPIX", 0);
        int width = GetInternalInt(header, "NAXIS1", 0);
        int height = GetInternalInt(header, "NAXIS2", 0);
        
        Debug.WriteLine($"[FitsReader] Geometria Rilevata: {width} x {height}, Bitpix: {bitpix}");

        double bzero = GetInternalDouble(header, "BZERO", 0.0);
        double bscale = GetInternalDouble(header, "BSCALE", 1.0);

        // Se dimensioni nulle (es. Primary Header vuoto), saltiamo i dati
        if (width <= 0 || height <= 0) 
        {
            Debug.WriteLine("[FitsReader] Immagine vuota (NAXIS=0). Salto blocco dati/heap.");
            SkipDataBlock(stream, header); 
            return Array.CreateInstance(typeof(byte), 0, 0);
        }

        Array result = null!;

        // Logica selezione tipo (con supporto Unsigned CCD)
        if (bitpix == 8) {
            Debug.WriteLine("[FitsReader] Tipo Dati: Byte (8-bit)");
            result = ReadBytePixels(stream, width, height);
        }
        else if (bitpix == 16)
        {
            if (IsUnsigned16(bzero, bscale)) {
                Debug.WriteLine("[FitsReader] Tipo Dati: UInt16 (CCD Raw - BZERO rilevato)");
                result = ReadUShortPixels(stream, width, height);
            } else {
                Debug.WriteLine("[FitsReader] Tipo Dati: Int16");
                result = ReadInt16Pixels(stream, width, height);
            }
        }
        else if (bitpix == 32)
        {
            if (IsUnsigned32(bzero, bscale)) {
                Debug.WriteLine("[FitsReader] Tipo Dati: UInt32");
                result = ReadUIntPixels(stream, width, height);
            } else {
                Debug.WriteLine("[FitsReader] Tipo Dati: Int32");
                result = ReadInt32Pixels(stream, width, height);
            }
        }
        else if (bitpix == -32) {
            Debug.WriteLine("[FitsReader] Tipo Dati: Float (32-bit)");
            result = ReadFloatPixels(stream, width, height);
        }
        else if (bitpix == -64) {
            Debug.WriteLine("[FitsReader] Tipo Dati: Double (64-bit)");
            result = ReadDoublePixels(stream, width, height);
        }
        else throw new NotSupportedException($"Formato BITPIX {bitpix} non supportato.");

        // Saltiamo il padding finale
        int bytesPerPixel = Math.Abs(bitpix) / 8;
        Debug.WriteLine($"[FitsReader] Matrice letta. Salto padding finale...");
        SkipPadding(stream, width, height, bytesPerPixel);

        return result;
    }

    public void SkipDataBlock(Stream stream, FitsHeader header)
    {
        long totalSkip = GetDataBlockLength(header);
        Debug.WriteLine($"[FitsReader] Skipping Data Block: {totalSkip} bytes.");
        
        if (totalSkip > 0)
        {
            if (stream.CanSeek)
            {
                long newPos = stream.Position + totalSkip;
                stream.Position = (newPos > stream.Length) ? stream.Length : newPos;
            }
            else
            {
                byte[] junk = new byte[Math.Min(totalSkip, 8192)];
                long toSkip = totalSkip;
                while (toSkip > 0)
                {
                    int read = stream.Read(junk, 0, (int)Math.Min(toSkip, junk.Length));
                    if (read == 0) break; 
                    toSkip -= read;
                }
            }
        }
    }

    public long GetDataBlockLength(FitsHeader header)
    {
        long dataSize = CalculateDataSizeInBytes(header);
        if (dataSize <= 0) return 0;
        long remainder = dataSize % BlockSize;
        long padding = (remainder == 0) ? 0 : (BlockSize - remainder);
        return dataSize + padding;
    }

    private long CalculateDataSizeInBytes(FitsHeader header)
    {
        int bitpix = Math.Abs(GetInternalInt(header, "BITPIX", 0));
        int naxis = GetInternalInt(header, "NAXIS", 0);
        long pcount = GetInternalLong(header, "PCOUNT", 0);
        long gcount = GetInternalLong(header, "GCOUNT", 1);

        if (naxis == 0 && pcount == 0) return 0;

        long numPixels = 1;
        bool hasAxes = false;

        for (int i = 1; i <= naxis; i++)
        {
            long axisLen = GetInternalLong(header, $"NAXIS{i}", 0);
            numPixels *= axisLen;
            hasAxes = true;
        }

        if (!hasAxes) numPixels = 0;

        long totalElements = (numPixels + pcount) * gcount;
        return (totalElements * bitpix) / 8;
    }

    private void SkipPadding(Stream stream, int w, int h, int bytesPerPixel)
    {
        long totalBytes = (long)w * h * bytesPerPixel;
        long remainder = totalBytes % BlockSize;
        if (remainder > 0)
        {
            long padding = BlockSize - remainder;
            Debug.WriteLine($"[FitsReader] Padding calcolato: {padding} bytes.");
            
            if (stream.CanSeek)
            {
                long target = stream.Position + padding;
                stream.Position = (target > stream.Length) ? stream.Length : target;
            }
            else
            {
                byte[] junk = new byte[padding];
                int offset = 0;
                while (offset < padding)
                {
                    int read = stream.Read(junk, offset, (int)(padding - offset));
                    if (read == 0) break;
                    offset += read;
                }
            }
        }
        else
        {
            Debug.WriteLine("[FitsReader] Nessun padding necessario (dati allineati a 2880).");
        }
    }

    // --- Helpers Lettura ---

    private int TryReadBlock(Stream s, byte[] buffer)
    {
        int offset = 0;
        int count = buffer.Length;
        while (count > 0)
        {
            int n = s.Read(buffer, offset, count);
            if (n == 0) break;
            offset += n; count -= n;
        }
        return offset;
    }

    private bool IsJunkBlock(byte[] buffer, int length)
    {
        for (int i = 0; i < length; i++)
            if (buffer[i] != 0 && buffer[i] != 32) return false;
        return true;
    }

    private void ReadExact(Stream s, byte[] b)
    {
        int offset = 0; int count = b.Length;
        while (count > 0) {
            int n = s.Read(b, offset, count);
            if (n == 0) throw new EndOfStreamException("Fine del file inaspettata durante la lettura dei pixel.");
            offset += n; count -= n;
        }
    }

    // --- Metodi Pixel (BigEndian -> Native) ---

    private byte[,] ReadBytePixels(Stream s, int w, int h) {
        var m = new byte[h, w]; byte[] buf = new byte[w];
        for (int y = 0; y < h; y++) { ReadExact(s, buf); for (int x = 0; x < w; x++) m[y, x] = buf[x]; }
        return m;
    }

    private short[,] ReadInt16Pixels(Stream s, int w, int h) {
        var m = new short[h, w]; byte[] buf = new byte[w * 2];
        for (int y = 0; y < h; y++) { ReadExact(s, buf); for (int x = 0; x < w; x++) m[y, x] = BinaryPrimitives.ReadInt16BigEndian(buf.AsSpan(x * 2)); }
        return m;
    }

    private ushort[,] ReadUShortPixels(Stream s, int w, int h) {
        var m = new ushort[h, w]; byte[] buf = new byte[w * 2];
        for (int y = 0; y < h; y++) { ReadExact(s, buf); for (int x = 0; x < w; x++) { 
            short val = BinaryPrimitives.ReadInt16BigEndian(buf.AsSpan(x * 2)); m[y, x] = (ushort)(val ^ 0x8000); } }
        return m;
    }

    private int[,] ReadInt32Pixels(Stream s, int w, int h) {
        var m = new int[h, w]; byte[] buf = new byte[w * 4];
        for (int y = 0; y < h; y++) { ReadExact(s, buf); for (int x = 0; x < w; x++) m[y, x] = BinaryPrimitives.ReadInt32BigEndian(buf.AsSpan(x * 4)); }
        return m;
    }

    private uint[,] ReadUIntPixels(Stream s, int w, int h) {
        var m = new uint[h, w]; byte[] buf = new byte[w * 4];
        for (int y = 0; y < h; y++) { ReadExact(s, buf); for (int x = 0; x < w; x++) {
            int val = BinaryPrimitives.ReadInt32BigEndian(buf.AsSpan(x * 4)); m[y, x] = (uint)(val ^ 0x80000000); } }
        return m;
    }

    private float[,] ReadFloatPixels(Stream s, int w, int h) {
        var m = new float[h, w]; byte[] buf = new byte[w * 4];
        for (int y = 0; y < h; y++) { ReadExact(s, buf); for (int x = 0; x < w; x++) m[y, x] = BinaryPrimitives.ReadSingleBigEndian(buf.AsSpan(x * 4)); }
        return m;
    }

    private double[,] ReadDoublePixels(Stream s, int w, int h) {
        var m = new double[h, w]; byte[] buf = new byte[w * 8];
        for (int y = 0; y < h; y++) { ReadExact(s, buf); for (int x = 0; x < w; x++) m[y, x] = BinaryPrimitives.ReadDoubleBigEndian(buf.AsSpan(x * 8)); }
        return m;
    }

    // --- Helpers Parsing ---
    private int GetInternalInt(FitsHeader h, string k, int d) => int.TryParse(GetVal(h, k), out int v) ? v : d;
    private long GetInternalLong(FitsHeader h, string k, long d) => long.TryParse(GetVal(h, k), out long v) ? v : d;
    private double GetInternalDouble(FitsHeader h, string k, double d) => double.TryParse(GetVal(h, k), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : d;
    private string GetVal(FitsHeader h, string k) => h.Cards.FirstOrDefault(c => c.Key.Equals(k, StringComparison.OrdinalIgnoreCase))?.Value ?? "";
    private bool IsUnsigned16(double z, double s) => Math.Abs(z - 32768.0) < 1.0 && Math.Abs(s - 1.0) < 0.001;
    private bool IsUnsigned32(double z, double s) => Math.Abs(z - 2147483648.0) < 1.0 && Math.Abs(s - 1.0) < 0.001;
}