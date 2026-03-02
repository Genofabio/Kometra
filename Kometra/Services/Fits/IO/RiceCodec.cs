using System;
using System.IO;
using System.Buffers.Binary;

namespace Kometra.Services.Fits.IO
{
    /// <summary>
    /// Porting C# completo (Encode/Decode) di 'ricecomp.c' e 'rdecomp.c' (CFITSIO).
    /// Gestisce decompressione Rice per 8, 16 e 32 bit con logica bit-exact compatibile con fpack.
    /// </summary>
    public static class RiceCodec
    {
        // Lookup table per il conteggio dei bit (da rcomp.c) - Conta la posizione del MSB
        // Usata in Decode per contare velocemente gli Zeri consecutivi (Leading Zeros)
        private static readonly int[] NonZeroCount = {
            0, 1, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 4, 4, 4, 4,
            5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5,
            6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6,
            6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6,
            7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7,
            7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7,
            7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7,
            7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7,
            8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8,
            8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8,
            8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8,
            8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8,
            8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8,
            8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8,
            8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8,
            8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8
        };

        // =======================================================================
        // DECODE METHODS (fits_rdecomp)
        // =======================================================================

        public static int[] Decode(byte[] c, int pixelCount, int bitPix, int blockSize = 32)
        {
            int absBitPix = Math.Abs(bitPix);
            if (absBitPix == 8) return DecodeByte(c, pixelCount, blockSize);
            if (absBitPix == 16) return DecodeShort(c, pixelCount, blockSize);
            return DecodeInt(c, pixelCount, blockSize);
        }

        private static int[] DecodeInt(byte[] c, int nx, int nblock)
        {
            int[] array = new int[nx];
            if (c == null || c.Length < 4) return array;

            int fsbits = 5;
            int fsmax = 25;
            int bbits = 1 << fsbits; // 32

            int cIdx = 0;
            // 32-bit seed
            int lastpix = BinaryPrimitives.ReadInt32BigEndian(c.AsSpan(cIdx));
            cIdx += 4;

            if (cIdx >= c.Length) { array[0] = lastpix; return array; }
            uint b = c[cIdx++];
            int nbits = 8;

            for (int i = 0; i < nx;)
            {
                nbits -= fsbits;
                while (nbits < 0)
                {
                    if (cIdx >= c.Length) break;
                    b = (b << 8) | c[cIdx++];
                    nbits += 8;
                }
                int fs = (int)(b >> nbits) - 1;
                b &= (uint)((1 << nbits) - 1);

                int imax = i + nblock;
                if (imax > nx) imax = nx;

                if (fs < 0)
                {
                    for (; i < imax; i++) array[i] = lastpix;
                }
                else if (fs == fsmax)
                {
                    for (; i < imax; i++)
                    {
                        int k = bbits - nbits;
                        uint diff = b << k;
                        for (k -= 8; k >= 0; k -= 8)
                        {
                            if (cIdx >= c.Length) break;
                            b = c[cIdx++];
                            diff |= (uint)(b << k);
                        }
                        if (nbits > 0)
                        {
                            if (cIdx < c.Length)
                            {
                                b = c[cIdx++];
                                diff |= (b >> (-k));
                                b &= (uint)((1 << nbits) - 1);
                            }
                        }
                        else b = 0;

                        if ((diff & 1) == 0) diff >>= 1;
                        else diff = ~(diff >> 1);

                        array[i] = (int)(diff + lastpix);
                        lastpix = array[i];
                    }
                }
                else
                {
                    for (; i < imax; i++)
                    {
                        // Conta Zeri consecutivi (fino al terminatore 1)
                        while (b == 0)
                        {
                            if (cIdx >= c.Length) break;
                            nbits += 8;
                            b = c[cIdx++];
                        }
                        int nzero = nbits - NonZeroCount[b];
                        nbits -= nzero + 1;
                        b ^= (uint)(1 << nbits);

                        nbits -= fs;
                        while (nbits < 0)
                        {
                            if (cIdx >= c.Length) break;
                            b = (b << 8) | c[cIdx++];
                            nbits += 8;
                        }
                        uint diff = (uint)((nzero << fs) | (b >> nbits));
                        b &= (uint)((1 << nbits) - 1);

                        if ((diff & 1) == 0) diff >>= 1;
                        else diff = ~(diff >> 1);

                        array[i] = (int)(diff + lastpix);
                        lastpix = array[i];
                    }
                }
            }
            return array;
        }

        private static int[] DecodeShort(byte[] c, int nx, int nblock)
        {
            int[] array = new int[nx];
            if (c == null || c.Length < 2) return array;

            int fsbits = 4;
            int fsmax = 14;
            int bbits = 1 << fsbits; // 16

            int cIdx = 0;
            // 16-bit seed
            int lastpix = BinaryPrimitives.ReadInt16BigEndian(c.AsSpan(cIdx));
            cIdx += 2;

            if (cIdx >= c.Length) { array[0] = lastpix; return array; }
            uint b = c[cIdx++];
            int nbits = 8;

            for (int i = 0; i < nx;)
            {
                nbits -= fsbits;
                while (nbits < 0)
                {
                    if (cIdx >= c.Length) break;
                    b = (b << 8) | c[cIdx++];
                    nbits += 8;
                }
                int fs = (int)(b >> nbits) - 1;
                b &= (uint)((1 << nbits) - 1);

                int imax = i + nblock;
                if (imax > nx) imax = nx;

                if (fs < 0)
                {
                    for (; i < imax; i++) array[i] = lastpix;
                }
                else if (fs == fsmax)
                {
                    for (; i < imax; i++)
                    {
                        int k = bbits - nbits;
                        uint diff = b << k;
                        for (k -= 8; k >= 0; k -= 8)
                        {
                            if (cIdx >= c.Length) break;
                            b = c[cIdx++];
                            diff |= (uint)(b << k);
                        }
                        if (nbits > 0)
                        {
                            if (cIdx < c.Length)
                            {
                                b = c[cIdx++];
                                diff |= (b >> (-k));
                                b &= (uint)((1 << nbits) - 1);
                            }
                        }
                        else b = 0;

                        if ((diff & 1) == 0) diff >>= 1;
                        else diff = ~(diff >> 1);

                        array[i] = (int)(diff + lastpix);
                        lastpix = array[i];
                    }
                }
                else
                {
                    for (; i < imax; i++)
                    {
                        // Conta Zeri consecutivi
                        while (b == 0)
                        {
                            if (cIdx >= c.Length) break;
                            nbits += 8;
                            b = c[cIdx++];
                        }
                        int nzero = nbits - NonZeroCount[b];
                        nbits -= nzero + 1;
                        b ^= (uint)(1 << nbits);

                        nbits -= fs;
                        while (nbits < 0)
                        {
                            if (cIdx >= c.Length) break;
                            b = (b << 8) | c[cIdx++];
                            nbits += 8;
                        }
                        uint diff = (uint)((nzero << fs) | (b >> nbits));
                        b &= (uint)((1 << nbits) - 1);

                        if ((diff & 1) == 0) diff >>= 1;
                        else diff = ~(diff >> 1);

                        array[i] = (int)(diff + lastpix);
                        lastpix = array[i];
                    }
                }
            }
            return array;
        }

        private static int[] DecodeByte(byte[] c, int nx, int nblock)
        {
            int[] array = new int[nx];
            if (c == null || c.Length < 1) return array;

            int fsbits = 3;
            int fsmax = 6;
            int bbits = 1 << fsbits; // 8

            int cIdx = 0;
            // 8-bit seed
            int lastpix = c[cIdx++];

            if (cIdx >= c.Length) { array[0] = lastpix; return array; }
            uint b = c[cIdx++];
            int nbits = 8;

            for (int i = 0; i < nx;)
            {
                nbits -= fsbits;
                while (nbits < 0)
                {
                    if (cIdx >= c.Length) break;
                    b = (b << 8) | c[cIdx++];
                    nbits += 8;
                }
                int fs = (int)(b >> nbits) - 1;
                b &= (uint)((1 << nbits) - 1);

                int imax = i + nblock;
                if (imax > nx) imax = nx;

                if (fs < 0)
                {
                    for (; i < imax; i++) array[i] = lastpix;
                }
                else if (fs == fsmax)
                {
                    for (; i < imax; i++)
                    {
                        int k = bbits - nbits;
                        uint diff = b << k;
                        for (k -= 8; k >= 0; k -= 8)
                        {
                            if (cIdx >= c.Length) break;
                            b = c[cIdx++];
                            diff |= (uint)(b << k);
                        }
                        if (nbits > 0)
                        {
                            if (cIdx < c.Length)
                            {
                                b = c[cIdx++];
                                diff |= (b >> (-k));
                                b &= (uint)((1 << nbits) - 1);
                            }
                        }
                        else b = 0;

                        if ((diff & 1) == 0) diff >>= 1;
                        else diff = ~(diff >> 1);

                        lastpix = (byte)(diff + lastpix);
                        array[i] = lastpix;
                    }
                }
                else
                {
                    for (; i < imax; i++)
                    {
                        // Conta Zeri consecutivi
                        while (b == 0)
                        {
                            if (cIdx >= c.Length) break;
                            nbits += 8;
                            b = c[cIdx++];
                        }
                        int nzero = nbits - NonZeroCount[b];
                        nbits -= nzero + 1;
                        b ^= (uint)(1 << nbits);

                        nbits -= fs;
                        while (nbits < 0)
                        {
                            if (cIdx >= c.Length) break;
                            b = (b << 8) | c[cIdx++];
                            nbits += 8;
                        }
                        uint diff = (uint)((nzero << fs) | (b >> nbits));
                        b &= (uint)((1 << nbits) - 1);

                        if ((diff & 1) == 0) diff >>= 1;
                        else diff = ~(diff >> 1);

                        lastpix = (byte)(diff + lastpix);
                        array[i] = lastpix;
                    }
                }
            }
            return array;
        }

        // =======================================================================
        // ENCODE METHODS (fits_rcomp)
        // =======================================================================

        /// <summary>
        /// Encode per dati 32-bit (INT/UINT/FLOAT quantizzato). Seed di 4 byte.
        /// </summary>
        public static byte[] Encode(int[] a, int nblock = 32)
        {
            if (a == null || a.Length == 0) return Array.Empty<byte>();
            
            using var ms = new MemoryStream(a.Length * 4 + 1024);
            
            // 32-bit: Seed di 4 byte (Big Endian)
            byte[] seedBytes = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(seedBytes, a[0]);
            ms.Write(seedBytes, 0, 4);

            var buf = new OutputBuffer(ms);
            int fsbits = 5;
            int fsmax = 25;
            int bbits = 32;

            int lastpix = a[0];
            int nx = a.Length;

            for (int i = 0; i < nx; i += nblock)
            {
                int thisblock = Math.Min(nblock, nx - i);
                long pixelsum = 0;
                uint[] diffs = new uint[thisblock];

                for (int j = 0; j < thisblock; j++)
                {
                    int nextpix = a[i + j];
                    long pdiff = (long)nextpix - (long)lastpix;
                    diffs[j] = (uint)((pdiff < 0) ? ~(pdiff << 1) : (pdiff << 1));
                    pixelsum += diffs[j];
                    lastpix = nextpix;
                }

                double dpsum = (pixelsum - (thisblock / 2.0) - 1.0) / thisblock;
                if (dpsum < 0) dpsum = 0.0;
                uint psum = (uint)((long)dpsum >> 1);
                int fs = 0;
                while (psum > 0) { fs++; psum >>= 1; }

                if (fs >= fsmax)
                {
                    buf.OutputNBits((uint)(fsmax + 1), fsbits);
                    for (int j = 0; j < thisblock; j++) buf.OutputNBits(diffs[j], bbits);
                }
                else if (fs == 0 && pixelsum == 0)
                {
                    buf.OutputNBits(0u, fsbits);
                }
                else
                {
                    buf.OutputNBits((uint)(fs + 1), fsbits);
                    for (int j = 0; j < thisblock; j++)
                    {
                        uint v = diffs[j];
                        int top = (int)(v >> fs);
                        
                        // --- UNARY FIX: Write Zeros, Terminate with One ---
                        // Matches decoder logic that counts leading zeros.
                        for(int k=0; k<top; k++) buf.OutputNBits(0, 1); 
                        buf.OutputNBits(1, 1); 

                        // Remainder part
                        if (fs > 0)
                        {
                            buf.OutputNBits(v & (uint)((1 << fs) - 1), fs);
                        }
                    }
                }
            }
            buf.Done();
            return ms.ToArray();
        }

        /// <summary>
        /// Encode per dati 16-bit (SHORT/USHORT). Seed di 2 byte.
        /// </summary>
        public static byte[] Encode(short[] a, int nblock = 32)
        {
            if (a == null || a.Length == 0) return Array.Empty<byte>();
            using var ms = new MemoryStream(a.Length * 2 + 1024);

            // 16-bit: Seed di 2 byte (Big Endian)
            byte[] seedBytes = new byte[2];
            BinaryPrimitives.WriteInt16BigEndian(seedBytes, a[0]);
            ms.Write(seedBytes, 0, 2);

            var buf = new OutputBuffer(ms);
            int fsbits = 4;
            int fsmax = 14;
            int bbits = 16;

            int lastpix = a[0];
            int nx = a.Length;

            for (int i = 0; i < nx; i += nblock)
            {
                int thisblock = Math.Min(nblock, nx - i);
                double pixelsum = 0;
                uint[] diffs = new uint[thisblock];

                for (int j = 0; j < thisblock; j++)
                {
                    int nextpix = a[i + j];
                    int pdiff = nextpix - lastpix;
                    diffs[j] = (uint)((pdiff < 0) ? ~(pdiff << 1) : (pdiff << 1));
                    pixelsum += diffs[j];
                    lastpix = nextpix;
                }

                double dpsum = (pixelsum - (thisblock / 2.0) - 1.0) / thisblock;
                if (dpsum < 0) dpsum = 0.0;
                ushort psum = (ushort)((long)dpsum >> 1);
                int fs = 0;
                while (psum > 0) { fs++; psum >>= 1; }

                if (fs >= fsmax)
                {
                    buf.OutputNBits((uint)(fsmax + 1), fsbits);
                    for (int j = 0; j < thisblock; j++) buf.OutputNBits(diffs[j], bbits);
                }
                else if (fs == 0 && pixelsum == 0)
                {
                    buf.OutputNBits(0u, fsbits);
                }
                else
                {
                    buf.OutputNBits((uint)(fs + 1), fsbits);
                    for (int j = 0; j < thisblock; j++)
                    {
                        uint v = diffs[j];
                        int top = (int)(v >> fs);
                        
                        // Unary Fix
                        for(int k=0; k<top; k++) buf.OutputNBits(0, 1);
                        buf.OutputNBits(1, 1);

                        // Remainder part
                        if (fs > 0)
                        {
                            buf.OutputNBits(v & (uint)((1 << fs) - 1), fs);
                        }
                    }
                }
            }
            buf.Done();
            return ms.ToArray();
        }

        /// <summary>
        /// Encode per dati 8-bit (BYTE/SBYTE). Seed di 1 byte.
        /// </summary>
        public static byte[] Encode(byte[] a, int nblock = 32)
        {
            if (a == null || a.Length == 0) return Array.Empty<byte>();
            using var ms = new MemoryStream(a.Length + 1024);

            // 8-bit: Seed di 1 byte
            ms.WriteByte(a[0]);

            var buf = new OutputBuffer(ms);
            int fsbits = 3;
            int fsmax = 6;
            int bbits = 8;

            int lastpix = a[0];
            int nx = a.Length;

            for (int i = 0; i < nx; i += nblock)
            {
                int thisblock = Math.Min(nblock, nx - i);
                double pixelsum = 0;
                uint[] diffs = new uint[thisblock];

                for (int j = 0; j < thisblock; j++)
                {
                    int nextpix = a[i + j];
                    // Casting sbyte fondamentale per wrap-around corretto su differenze negative
                    int pdiff = (sbyte)nextpix - (sbyte)lastpix;
                    
                    diffs[j] = (uint)((pdiff < 0) ? ~(pdiff << 1) : (pdiff << 1));
                    pixelsum += diffs[j];
                    lastpix = nextpix;
                }

                double dpsum = (pixelsum - (thisblock / 2.0) - 1.0) / thisblock;
                if (dpsum < 0) dpsum = 0.0;
                byte psum = (byte)((long)dpsum >> 1);
                int fs = 0;
                while (psum > 0) { fs++; psum >>= 1; }

                if (fs >= fsmax)
                {
                    buf.OutputNBits((uint)(fsmax + 1), fsbits);
                    for (int j = 0; j < thisblock; j++) buf.OutputNBits(diffs[j], bbits);
                }
                else if (fs == 0 && pixelsum == 0)
                {
                    buf.OutputNBits(0u, fsbits);
                }
                else
                {
                    buf.OutputNBits((uint)(fs + 1), fsbits);
                    for (int j = 0; j < thisblock; j++)
                    {
                        uint v = diffs[j];
                        int top = (int)(v >> fs);
                        
                        // Unary Fix
                        for(int k=0; k<top; k++) buf.OutputNBits(0, 1);
                        buf.OutputNBits(1, 1);

                        // Remainder part
                        if (fs > 0)
                        {
                            buf.OutputNBits(v & (uint)((1 << fs) - 1), fs);
                        }
                    }
                }
            }
            buf.Done();
            return ms.ToArray();
        }

        // =======================================================================
        // OUTPUT BUFFER HELPER CLASS (Big Endian Bit Stream)
        // =======================================================================
        private class OutputBuffer
        {
            private readonly MemoryStream _stream;
            private byte _currentByte;
            private int _bitsFilled;

            public OutputBuffer(MemoryStream s)
            {
                _stream = s;
                _currentByte = 0;
                _bitsFilled = 0;
            }

            public void OutputNBits(uint bits, int n)
            {
                // Scrive n bit (MSB first) nello stream.
                // Es. se bits=3 (0...011) e n=5 -> scrive 00011
                for (int i = n - 1; i >= 0; i--)
                {
                    // Estrai l'i-esimo bit
                    uint bit = (bits >> i) & 1;

                    // Se è 1, lo posizioniamo nel byte corrente
                    // Riempiamo da 7 (MSB) a 0 (LSB)
                    if (bit != 0)
                    {
                        _currentByte |= (byte)(1 << (7 - _bitsFilled));
                    }

                    _bitsFilled++;

                    if (_bitsFilled == 8)
                    {
                        _stream.WriteByte(_currentByte);
                        _currentByte = 0;
                        _bitsFilled = 0;
                    }
                }
            }

            public void Done()
            {
                // Se rimangono bit (padding), scriviamo l'ultimo byte.
                // I bit rimanenti sono già a 0 (perché _currentByte viene resettato a 0),
                // che è il padding standard corretto (zero-padding dei bit bassi inutilizzati).
                if (_bitsFilled > 0)
                {
                    _stream.WriteByte(_currentByte);
                }
            }
        }
    }
}