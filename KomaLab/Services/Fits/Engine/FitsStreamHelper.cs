using System;
using System.Buffers.Binary;

namespace KomaLab.Services.Fits.Engine;

/// <summary>
/// Utility di basso livello per gestire l'Endianness (Big-Endian vs Little-Endian).
/// FITS è Big-Endian, l'architettura Intel/AMD (.NET) è Little-Endian.
/// </summary>
public static class FitsStreamHelper
{
    // --- LETTURA (Big Endian -> Little Endian) ---

    public static short ReadInt16(ReadOnlySpan<byte> buffer)
    {
        // Legge 2 byte e li inverte se necessario
        return BinaryPrimitives.ReadInt16BigEndian(buffer);
    }

    public static int ReadInt32(ReadOnlySpan<byte> buffer)
    {
        // Legge 4 byte e li inverte
        return BinaryPrimitives.ReadInt32BigEndian(buffer);
    }

    public static float ReadFloat(ReadOnlySpan<byte> buffer)
    {
        // I float sono più complessi: leggiamo come Int32 e facciamo il cast
        // BinaryPrimitives gestisce lo standard IEEE 754
        return BinaryPrimitives.ReadSingleBigEndian(buffer);
    }

    public static double ReadDouble(ReadOnlySpan<byte> buffer)
    {
        // Legge 8 byte
        return BinaryPrimitives.ReadDoubleBigEndian(buffer);
    }

    // --- SCRITTURA (Little Endian -> Big Endian) ---

    public static void WriteInt16(Span<byte> buffer, short value)
    {
        BinaryPrimitives.WriteInt16BigEndian(buffer, value);
    }

    public static void WriteInt32(Span<byte> buffer, int value)
    {
        BinaryPrimitives.WriteInt32BigEndian(buffer, value);
    }
    
    public static void WriteFloat(Span<byte> buffer, float value)
    {
        BinaryPrimitives.WriteSingleBigEndian(buffer, value);
    }

    public static void WriteDouble(Span<byte> buffer, double value)
    {
        BinaryPrimitives.WriteDoubleBigEndian(buffer, value);
    }
}