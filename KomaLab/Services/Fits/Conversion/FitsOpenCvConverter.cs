using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using KomaLab.Models.Fits;
using KomaLab.Models.Fits.Structure;
using OpenCvSharp;

namespace KomaLab.Services.Fits.Conversion;

public class FitsOpenCvConverter : IFitsOpenCvConverter
{
    public Mat RawToMat(Array rawPixels, double bScale = 1.0, double bZero = 0.0, FitsBitDepth? targetDepth = null)
    {
        if (rawPixels == null) throw new ArgumentNullException(nameof(rawPixels));

        int rows = rawPixels.GetLength(0);
        int cols = rawPixels.GetLength(1);

        // --- DIAGNOSTICA VELOCE ---
        // Rimosso loop min/max per massimizzare la velocità
        Debug.WriteLine($"[FitsConverter] Fast Path: {cols}x{rows}, Type: {rawPixels.GetType().Name}");

        if (rows == 0 || cols == 0) return new Mat();

        // Determina profondità target (32 o 64 bit Float)
        int depthBit = targetDepth.HasValue 
            ? (targetDepth.Value == FitsBitDepth.Float ? 32 : 64) 
            : DetermineOptimalDepth(rawPixels);
            
        MatType destType = (depthBit == 32) ? MatType.CV_32FC1 : MatType.CV_64FC1;

        // FIX UNIFORMITÀ: Se arriva unsigned nativo, azzeriamo bZero (opzionale)
        if (rawPixels is ushort[,] || rawPixels is uint[,]) bZero = 0.0;

        // =========================================================
        // IL CUORE DELL'OTTIMIZZAZIONE: GCHandle (Zero-Copy)
        // =========================================================
        
        // 1. PINNING: Blocchiamo l'array C# in memoria.
        GCHandle handle = GCHandle.Alloc(rawPixels, GCHandleType.Pinned);

        try
        {
            IntPtr rawDataPtr = handle.AddrOfPinnedObject();
            Type type = rawPixels.GetType().GetElementType();

            // 2. WRAPPER: Creiamo una "vista" OpenCV sulla memoria C# esistente.
            //    Usiamo Mat.FromPixelData come richiesto dalle nuove versioni.
            using Mat rawWrapper = CreateWrapperWithFix(rows, cols, type, rawDataPtr);

            // 3. CONVERSIONE: L'unica operazione pesante (SIMD ottimizzata).
            Mat result = new Mat(rows, cols, destType);
            rawWrapper.ConvertTo(result, destType, bScale, bZero);

            return result;
        }
        finally
        {
            // 4. CLEANUP: Sblocchiamo l'array C#. Fondamentale!
            if (handle.IsAllocated) handle.Free();
        }
    }

    /// <summary>
    /// Crea un Wrapper OpenCV attorno ai dati C# usando Mat.FromPixelData.
    /// Include il FIX CRITICO per la solarizzazione (Short -> CV_16U).
    /// </summary>
    private Mat CreateWrapperWithFix(int rows, int cols, Type elementType, IntPtr ptr)
    {
        // === FIX CRITICO "SOLE NERO" ===
        // Se C# dice "short" (Signed), noi diciamo a OpenCV "ushort" (CV_16U).
        // Mat.FromPixelData accetta il puntatore raw e si fida del MatType che gli passiamo.
        if (elementType == typeof(short)) 
            return Mat.FromPixelData(rows, cols, MatType.CV_16UC1, ptr);

        // Gestione Standard
        if (elementType == typeof(ushort)) return Mat.FromPixelData(rows, cols, MatType.CV_16UC1, ptr);
        if (elementType == typeof(byte)) return Mat.FromPixelData(rows, cols, MatType.CV_8UC1, ptr);
        if (elementType == typeof(sbyte)) return Mat.FromPixelData(rows, cols, MatType.CV_8SC1, ptr);
        if (elementType == typeof(int)) return Mat.FromPixelData(rows, cols, MatType.CV_32SC1, ptr);
        if (elementType == typeof(uint)) return Mat.FromPixelData(rows, cols, MatType.CV_32SC1, ptr); 
        if (elementType == typeof(float)) return Mat.FromPixelData(rows, cols, MatType.CV_32FC1, ptr);
        if (elementType == typeof(double)) return Mat.FromPixelData(rows, cols, MatType.CV_64FC1, ptr);

        throw new NotSupportedException($"Tipo {elementType} non supportato per Fast-Path.");
    }

    private int DetermineOptimalDepth(Array rawPixels)
    {
        return rawPixels switch
        {
            int[,] or uint[,] or double[,] => 64, // Preserva precisione Double/Int32
            _ => 32 // Float basta per Byte, Short, UShort
        };
    }

    public Array MatToRaw(Mat mat, FitsBitDepth targetDepth = FitsBitDepth.Double)
    {
        if (mat.Empty()) return Array.CreateInstance(typeof(byte), 0, 0);
        
        // Puoi lasciare qui la tua vecchia implementazione di MatToRaw
        // se ti serve per salvare i file, oppure implementare una versione simile
        // usando Mat.GetArray(out type[] arr) se OpenCvSharp lo supporta per quel tipo.
        // Per ora, concentriamoci sulla lettura veloce.
        throw new NotImplementedException("Implementa l'export se necessario usando la logica precedente.");
    }
}