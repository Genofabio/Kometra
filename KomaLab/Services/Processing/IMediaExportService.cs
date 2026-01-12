using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KomaLab.Models.Visualization;
using OpenCvSharp;

// Per VisualizationMode

namespace KomaLab.Services.Processing;

public interface IMediaExportService
{
    /// <summary>
    /// Genera un file video (MJPG) a partire da una sequenza di file FITS.
    /// Carica ogni file, applica lo stretch (contrasto) e salva il frame.
    /// </summary>
    /// <param name="sourceFiles">Lista dei percorsi completi dei file FITS.</param>
    /// <param name="outputFilePath">Percorso destinazione video (.avi).</param>
    /// <param name="fps">Frame rate del video.</param>
    /// <param name="profile">Profilo di contrasto (Assoluto o Relativo).</param>
    /// <param name="mode">Modalità di visualizzazione (Linear, Log, Sqrt).</param>
    Task ExportVideoAsync(
        List<string> sourceFiles, 
        string outputFilePath, 
        double fps,
        ContrastProfile profile,
        VisualizationMode mode = VisualizationMode.Linear);

    /// <summary>
    /// Rendering diretto in memoria per la UI (Preview).
    /// Prende una matrice scientifica (Double) e scrive i pixel a 8-bit (visualizzabili)
    /// direttamente in un buffer di memoria (es. WriteableBitmap.BackBuffer).
    /// </summary>
    /// <param name="sourceMat">Matrice sorgente (formato Double CV_64FC1).</param>
    /// <param name="width">Larghezza immagine.</param>
    /// <param name="height">Altezza immagine.</param>
    /// <param name="blackPoint">Valore di cut-off nero (ADU).</param>
    /// <param name="whitePoint">Valore di cut-off bianco (ADU).</param>
    /// <param name="destinationBuffer">Puntatore alla memoria video (IntPtr).</param>
    /// <param name="stride">Stride (larghezza riga in byte) del buffer destinazione.</param>
    /// <param name="mode">Modalità di visualizzazione.</param>
    void RenderToBuffer(
        Mat sourceMat, 
        int width, 
        int height, 
        double blackPoint, 
        double whitePoint, 
        IntPtr destinationBuffer, 
        long stride,
        VisualizationMode mode);
}