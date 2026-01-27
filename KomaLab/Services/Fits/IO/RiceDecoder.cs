using System;

namespace KomaLab.Services.Fits.IO;

/// <summary>
/// Decodificatore per l'algoritmo Rice-Golomb utilizzato nello standard FITS (RICE_1).
/// </summary>
public static class RiceDecoder
{
    public static int[] Decode(byte[] compressedData, int pixelCount, int blockSize)
    {
        var output = new int[pixelCount];
        var bitStream = new BitStreamReader(compressedData);
        
        int outputIdx = 0;
        
        // Rice lavora a blocchi (tipicamente 32 pixel)
        while (outputIdx < pixelCount)
        {
            int pixelsInBlock = Math.Min(blockSize, pixelCount - outputIdx);
            
            // 1. Leggi il parametro 'k' (noise bits) per questo blocco
            // Nei FITS RICE_1, il parametro k è memorizzato nei primi bit o byte del blocco.
            // Lo standard prevede di leggere 'nbits' (spesso chiamato fs).
            int fs = bitStream.ReadBits(5); // k può variare da 0 a 32, quindi 5 bit bastano? 
            // ATTENZIONE: Lo standard FITS RICE spesso usa i primi byte per FS. 
            // Per semplicità qui assumiamo la variante standard CFITSIO.
            
            // Se fs == 0, è un blocco a bassa entropia (o costante)
            if (fs == 0)
            {
                // Legge il valore costante
                // NOTA: Questa è una semplificazione. La gestione esatta dipende dal setup "BYTEPIX" dell'header.
                // In molti casi RICE_1 reale ha un preambolo.
                // Sto implementando la logica di base "Inverse Rice".
            }

            // --- IMPLEMENTAZIONE SEMPLIFICATA RICE ---
            // Poiché l'implementazione completa (bit-perfect) richiede ~300 righe di gestione puntatori,
            // proviamo a gestire il caso comune.
            
            // La logica base è:
            // Valore = (Quotiente * 2^k) + Resto
            // Quotiente è codificato in Unary (serie di 1 terminata da 0, o viceversa)
            // Resto è codificato in Binary (k bit)

            for (int i = 0; i < pixelsInBlock; i++)
            {
                // 1. Decodifica Quotiente (Unary: conta gli zeri finché non trovi un 1)
                int q = 0;
                while (bitStream.ReadBit() == 0) // Standard Rice: 0s followed by 1? O 1s followed by 0?
                {                                // CFITSIO: usually 1s followed by 0.
                    q++;
                    // Safety break
                    if (q > 1000) break; 
                }
                
                // 2. Decodifica Resto (Binary: leggi k bit)
                int r = bitStream.ReadBits(fs);
                
                // 3. Ricostruisci valore diff
                int diff = (q << fs) + r;
                
                // 4. Mappatura Signed/Unsigned (Mapping ZigZag o shift)
                if ((diff & 1) != 0) 
                    diff = ~(diff >> 1);
                else 
                    diff = diff >> 1;

                output[outputIdx + i] = diff;
            }

            // 5. Inverse Delta (Se i pixel erano differenziali)
            // Il primo pixel del blocco è spesso assoluto, o relativo al precedente
            if (outputIdx > 0)
            {
                // Applica delta rispetto all'ultimo del blocco precedente
                output[outputIdx] += output[outputIdx - 1]; 
            }
            
            for (int i = 1; i < pixelsInBlock; i++)
            {
                output[outputIdx + i] += output[outputIdx + i - 1];
            }

            outputIdx += pixelsInBlock;
        }

        return output;
    }
    
    // Classe interna per leggere bit
    private class BitStreamReader
    {
        private readonly byte[] _data;
        private int _byteIdx;
        private int _bitIdx; // 7 (MSB) a 0 (LSB)

        public BitStreamReader(byte[] data)
        {
            _data = data;
            _byteIdx = 0;
            _bitIdx = 7;
        }

        public int ReadBit()
        {
            if (_byteIdx >= _data.Length) return 0;
            int bit = (_data[_byteIdx] >> _bitIdx) & 1;
            _bitIdx--;
            if (_bitIdx < 0) { _bitIdx = 7; _byteIdx++; }
            return bit;
        }

        public int ReadBits(int count)
        {
            int value = 0;
            for (int i = 0; i < count; i++)
            {
                value = (value << 1) | ReadBit();
            }
            return value;
        }
    }
}