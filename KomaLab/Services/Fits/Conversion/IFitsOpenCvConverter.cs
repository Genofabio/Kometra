using System;
using KomaLab.Models.Fits;
using KomaLab.Models.Fits.Structure;
using OpenCvSharp;

// Non strettamente necessario qui, ma buona pratica nei using

namespace KomaLab.Services.Fits.Conversion;

// ---------------------------------------------------------------------------
// FILE: IFitsOpenCvConverter.cs
// DESCRIZIONE:
// Convertitore puro di dati binari (Pixel Data).
// Si occupa esclusivamente della trasformazione ad alte prestazioni tra:
// - Array C# gestiti (formato FITS raw)
// - Matrici OpenCV non gestite (formato elaborazione)
// NON gestisce i metadati o la logica di header (delegata a IFitsMetadataService).
// ---------------------------------------------------------------------------

public interface IFitsOpenCvConverter
{
    // Default values per BScale/BZero. Parametri opzionali sempre in fondo.
    Mat RawToMat(Array rawPixels, double bScale = 1.0, double bZero = 0.0);
    
    // yStart/rowsToRead sono obbligatori per il rect, quindi vanno prima degli opzionali
    Mat RawToMatRect(Array rawPixels, int yStart, int rowsToRead, double bScale = 1.0, double bZero = 0.0);

    // Restituisce SOLO l'array dei pixel.
    Array MatToRaw(Mat mat, FitsBitDepth targetDepth = FitsBitDepth.Double);
}