using System;
using System.IO;
using System.Linq;

namespace KomaLab.Services.Fits.IO;

/// <summary>
/// Codec per l'algoritmo Rice-Golomb utilizzato nello standard FITS (RICE_1).
/// Gestisce sia la decompressione (Decode) che la compressione (Encode) di flussi di interi.
/// </summary>
public static class RiceCodec
{
    // =======================================================================
    // 1. DECOMPRESSIONE (DECODE)
    // =======================================================================
    
    public static int[] Decode(byte[] compressedData, int pixelCount, int blockSize = 32)
    {
        var output = new int[pixelCount];
        if (compressedData == null || compressedData.Length == 0) return output;

        var bitStream = new BitStreamReader(compressedData);
        int outputIdx = 0;
        int lastValue = 0; 

        while (outputIdx < pixelCount)
        {
            int pixelsInBlock = Math.Min(blockSize, pixelCount - outputIdx);
            int fs = bitStream.ReadBits(5); 

            if (fs == 0)
            {
                for (int i = 0; i < pixelsInBlock; i++) output[outputIdx + i] = lastValue;
                outputIdx += pixelsInBlock;
                continue;
            }

            if (fs == 31)
            {
                for (int i = 0; i < pixelsInBlock; i++) output[outputIdx + i] = bitStream.ReadBits(32);
                lastValue = output[outputIdx + pixelsInBlock - 1];
                outputIdx += pixelsInBlock;
                continue;
            }

            for (int i = 0; i < pixelsInBlock; i++)
            {
                int q = 0;
                while (bitStream.ReadBit() == 1) { q++; if (q > 1024) break; }
                int r = bitStream.ReadBits(fs);
                
                int diff = (q << fs) + r;
                int finalDiff = (diff & 1) != 0 ? ~(diff >> 1) : diff >> 1;

                int actualValue = lastValue + finalDiff;
                output[outputIdx + i] = actualValue;
                lastValue = actualValue;
            }
            outputIdx += pixelsInBlock;
        }
        return output;
    }

    // =======================================================================
    // 2. COMPRESSIONE (ENCODE)
    // =======================================================================

    /// <summary>
    /// Comprime un array di interi utilizzando l'algoritmo Rice_1.
    /// </summary>
    public static byte[] Encode(int[] data, int blockSize = 32)
    {
        if (data == null || data.Length == 0) return Array.Empty<byte>();

        using var ms = new MemoryStream();
        var bitWriter = new BitStreamWriter(ms);
        int lastValue = 0;

        for (int i = 0; i < data.Length; i += blockSize)
        {
            int pixelsInBlock = Math.Min(blockSize, data.Length - i);
            
            // A. Preparazione: Delta e Mapping ZigZag
            uint[] mappedValues = new uint[pixelsInBlock];
            long absSum = 0;

            for (int j = 0; j < pixelsInBlock; j++)
            {
                int val = data[i + j];
                int diff = val - lastValue;
                lastValue = val;
                
                // ZigZag Mapping per rendere tutto positivo
                mappedValues[j] = (uint)((diff << 1) ^ (diff >> 31));
                absSum += mappedValues[j];
            }

            // B. Selezione parametro k (fs) ottimale basata sulla media del blocco
            int fs = 0;
            if (absSum > 0)
            {
                double average = (double)absSum / pixelsInBlock;
                // Heuristic per k: log2(media)
                fs = Math.Clamp((int)Math.Log2(average), 0, 30);
            }

            // C. Scrittura parametro fs (5 bit)
            bitWriter.WriteBits(fs, 5);

            // D. Codifica Rice-Golomb
            foreach (uint val in mappedValues)
            {
                uint q = val >> fs;
                uint r = val & (uint)((1 << fs) - 1);

                // Quoziente in Unario (serie di '1' terminata da '0')
                for (int b = 0; b < q; b++) bitWriter.WriteBit(1);
                bitWriter.WriteBit(0);

                // Resto in Binario (fs bit)
                bitWriter.WriteBits((int)r, fs);
            }
        }

        bitWriter.Flush();
        return ms.ToArray();
    }

    // =======================================================================
    // HELPERS: BIT STREAMING
    // =======================================================================

    private class BitStreamReader
    {
        private readonly byte[] _data;
        private int _byteIdx, _bitIdx = 7;
        public BitStreamReader(byte[] d) => _data = d;
        public int ReadBit() {
            if (_byteIdx >= _data.Length) return 0;
            int bit = (_data[_byteIdx] >> _bitIdx) & 1;
            if (--_bitIdx < 0) { _bitIdx = 7; _byteIdx++; }
            return bit;
        }
        public int ReadBits(int n) {
            int v = 0;
            for (int i = 0; i < n; i++) v = (v << 1) | ReadBit();
            return v;
        }
    }

    private class BitStreamWriter
    {
        private readonly Stream _stream;
        private byte _currentByte;
        private int _bitPos = 7;

        public BitStreamWriter(Stream s) => _stream = s;

        public void WriteBit(int bit)
        {
            if (bit != 0) _currentByte |= (byte)(1 << _bitPos);
            if (--_bitPos < 0)
            {
                _stream.WriteByte(_currentByte);
                _currentByte = 0;
                _bitPos = 7;
            }
        }

        public void WriteBits(int val, int count)
        {
            for (int i = count - 1; i >= 0; i--)
                WriteBit((val >> i) & 1);
        }

        public void Flush()
        {
            if (_bitPos != 7) _stream.WriteByte(_currentByte);
        }
    }
}