using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using KomaLab.Models.Visualization;
using KomaLab.Services.Fits;
using OpenCvSharp;
// Namespace corretto per ContrastProfile/VisualizationMode

namespace KomaLab.Services.Processing;

// ---------------------------------------------------------------------------
// FILE: MediaExportService.cs
// RUOLO: Generatore Output Visuale (Imaging Engine)
// DESCRIZIONE:
// Si occupa della trasformazione "distruttiva" dai dati scientifici (Double 64-bit)
// a formati di visualizzazione "consumer" (Video 8-bit, Buffer UI).
// 
// RESPONSABILITÀ:
// 1. Applicazione Stretch/Contrasto (Linear, Log, Sqrt).
// 2. Mapping dei livelli ADU (Black/White point) nel range visibile 0-255.
// 3. Generazione file video (.avi) e rendering su buffer UI.
// 4. Gestione delle differenze di coordinate (Flip cartesiano vs raster).
// ---------------------------------------------------------------------------

public class MediaExportService : IMediaExportService
{
    private readonly IFitsIoService _ioService;
    private readonly IFitsImageDataConverter _converter;

    public MediaExportService(
        IFitsIoService ioService, 
        IFitsImageDataConverter converter)
    {
        _ioService = ioService ?? throw new ArgumentNullException(nameof(ioService));
        _converter = converter ?? throw new ArgumentNullException(nameof(converter));
    }

    public async Task ExportVideoAsync(
        List<string> sourceFiles, 
        string outputFilePath, 
        double fps,
        ContrastProfile profile,
        VisualizationMode mode)
    {
        // Eseguiamo in un thread separato per non bloccare la UI durante l'encoding
        await Task.Run(async () =>
        {
            if (sourceFiles.Count == 0) return;

            VideoWriter? writer = null;
            Mat? frame8Bit = null;
            
            try
            {
                // 1. Inizializzazione VideoWriter
                // Carichiamo il primo frame per ottenere le dimensioni corrette.
                // Nota: Non usiamo ReadHeaderOnly perché ci servono le dimensioni reali dopo l'eventuale crop/load.
                var firstInfo = await _ioService.LoadAsync(sourceFiles[0]);
                if (firstInfo == null) throw new IOException($"Impossibile leggere il primo file: {sourceFiles[0]}");

                var size = new Size(firstInfo.Width, firstInfo.Height);
                
                // Codec MJPG: Robusto, lossless-like, supporta grayscale nativo.
                // Ottimo per analisi scientifica "veloce" senza compressione temporale eccessiva (come H.264).
                int fourcc = VideoWriter.FourCC('M', 'J', 'P', 'G'); 
                
                // isColor: false forza il writer in modalità monocromatica (ottimizza spazio)
                writer = new VideoWriter(outputFilePath, fourcc, fps, size, isColor: false);
                
                if (!writer.IsOpened()) 
                    throw new IOException("Errore inizializzazione VideoWriter OpenCV. Verifica i codec installati.");

                // Buffer riutilizzabile per il rendering 8-bit finale
                frame8Bit = new Mat(size.Height, size.Width, MatType.CV_8UC1);

                // 2. Loop di elaborazione frame
                foreach (string path in sourceFiles)
                {
                    // A. Caricamento Dati Scientifici (Layer DATA)
                    var fitsData = await _ioService.LoadAsync(path);
                    if (fitsData == null) continue;
                    
                    // Skip frame con dimensioni errate (evita crash hard di OpenCV)
                    if (fitsData.Width != size.Width || fitsData.Height != size.Height) continue;

                    // B. Conversione in Matrice Matematica (Layer DATA -> IMAGING)
                    using Mat scientificMat = _converter.RawToMat(fitsData);
                    
                    // 
                    // Operazioni correttive indispensabili per la visualizzazione:
                    Cv2.PatchNaNs(scientificMat);       // 1. Sostituisce NaN con 0 (fix bordi neri)
                    Cv2.Flip(scientificMat, scientificMat, FlipMode.X); // 2. Flip Verticale (FITS è bottom-up)

                    // C. Calcolo Livelli di Contrasto (Stretch)
                    double black, white;

                    if (profile is AbsoluteContrastProfile abs)
                    {
                        // L'utente ha fissato i valori manualmente (es. da istogramma globale)
                        black = abs.BlackADU;
                        white = abs.WhiteADU;
                    }
                    else if (profile is RelativeContrastProfile rel)
                    {
                        // Auto-Stretch dinamico frame-by-frame
                        // (Utile per timelapse dove la luminosità del cielo cambia)
                        Cv2.MinMaxLoc(scientificMat, out double min, out double max, out _, out _);
                        double range = max - min;
                        black = min + (range * rel.LowerPercentile);
                        white = min + (range * rel.UpperPercentile);
                    }
                    else
                    {
                        // Fallback difensivo (mostra tutto il range 16-bit)
                        black = 0; white = 65535;
                    }

                    // D. Rendering Visuale (Double -> Byte)
                    RenderFrameTo8Bit(scientificMat, frame8Bit, black, white, mode);

                    // E. Scrittura su disco
                    writer.Write(frame8Bit);
                }
            }
            finally
            {
                // Pulizia risorse non gestite
                writer?.Dispose();
                frame8Bit?.Dispose();
            }
        });
    }

    public void RenderToBuffer(
        Mat sourceMat, 
        int width, 
        int height, 
        double blackPoint, 
        double whitePoint, 
        IntPtr destinationBuffer, 
        long stride,
        VisualizationMode mode)
    {
        if (sourceMat.Empty()) return;

        // Crea un wrapper Mat attorno al buffer di memoria della UI (Zero-Copy).
        // Scriviamo direttamente nella memoria video della Bitmap WPF/Avalonia.
        // MatType.CV_8UC1 implica che la Bitmap destinazione deve essere Gray8 o simile.
        using Mat dstMat = Mat.FromPixelData(height, width, MatType.CV_8UC1, destinationBuffer, stride);
        
        RenderFrameTo8Bit(sourceMat, dstMat, blackPoint, whitePoint, mode);
    }

    /// <summary>
    /// Logica Core di visualizzazione. 
    /// Mappa i valori Double [black...white] nel range Byte [0...255].
    /// </summary>
    private void RenderFrameTo8Bit(Mat src, Mat dst8Bit, double black, double white, VisualizationMode mode)
    {
        double range = white - black;
        // Evitiamo divisione per zero se l'immagine è piatta (es. dark frame sintetico)
        if (Math.Abs(range) < 1e-9) range = 1e-5; 

        if (mode == VisualizationMode.Linear)
        {
            // Formula Lineare: Out = (In * alpha) + beta
            // Vogliamo mappare: Black -> 0, White -> 255
            double alpha = 255.0 / range;
            double beta = -black * alpha;
            
            // Protezione contro Overflow matematico (Infinity)
            if (double.IsInfinity(alpha)) alpha = 0;
            if (double.IsInfinity(beta)) beta = 0;

            // ConvertTo applica la trasformazione lineare ottimizzata SIMD
            src.ConvertTo(dst8Bit, MatType.CV_8UC1, alpha, beta);
        }
        else
        {
            // Logica Non-Lineare (Log, Sqrt) richiede normalizzazione preliminare [0..1]
            double scale = 1.0 / range;
            double offset = -black * scale;

            using Mat tempMat = new Mat();
            
            // 1. Normalizza nel range [0.0 ... 1.0]
            src.ConvertTo(tempMat, MatType.CV_32FC1, scale, offset);

            // 2. Clipping (taglia valori < 0 e > 1 per evitare artefatti matematici)
            Cv2.Threshold(tempMat, tempMat, 0, 0, ThresholdTypes.Tozero); // < 0 diventa 0
            Cv2.Threshold(tempMat, tempMat, 1, 1, ThresholdTypes.Trunc);  // > 1 diventa 1

            // 3. Applicazione Funzione di Trasferimento
            if (mode == VisualizationMode.SquareRoot)
            {
                Cv2.Sqrt(tempMat, tempMat);
            }
            else if (mode == VisualizationMode.Logarithmic)
            {
                // Scaling percettivo: log2(1 + x)
                // Approssimato come: ln(1+x) * 1.4427
                Cv2.Add(tempMat, 1.0, tempMat);
                Cv2.Log(tempMat, tempMat);
                Cv2.Multiply(tempMat, 1.442695, tempMat);
            }

            // 4. Scala finale a 8 bit [0...255]
            tempMat.ConvertTo(dst8Bit, MatType.CV_8UC1, 255.0);
        }
    }
}