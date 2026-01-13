using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using KomaLab.Models.Visualization;
using KomaLab.Services.Fits;
using OpenCvSharp;

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
    private readonly IImageAnalysisService _analysis; // NUOVO: Per statistiche Sigma

    public MediaExportService(
        IFitsIoService ioService, 
        IFitsImageDataConverter converter,
        IImageAnalysisService analysis) // Iniettato
    {
        _ioService = ioService ?? throw new ArgumentNullException(nameof(ioService));
        _converter = converter ?? throw new ArgumentNullException(nameof(converter));
        _analysis = analysis ?? throw new ArgumentNullException(nameof(analysis));
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
                var firstInfo = await _ioService.LoadAsync(sourceFiles[0]);
                if (firstInfo == null) throw new IOException($"Impossibile leggere il primo file: {sourceFiles[0]}");

                var size = new Size(firstInfo.Width, firstInfo.Height);
                int fourcc = VideoWriter.FourCC('M', 'J', 'P', 'G'); 
                
                writer = new VideoWriter(outputFilePath, fourcc, fps, size, isColor: false);
                
                if (!writer.IsOpened()) 
                    throw new IOException("Errore inizializzazione VideoWriter OpenCV.");

                frame8Bit = new Mat(size.Height, size.Width, MatType.CV_8UC1);

                // 2. Loop di elaborazione frame
                foreach (string path in sourceFiles)
                {
                    // A. Caricamento Dati Scientifici
                    var fitsData = await _ioService.LoadAsync(path);
                    if (fitsData == null) continue;
                    
                    if (fitsData.Width != size.Width || fitsData.Height != size.Height) continue;

                    // B. Conversione in Matrice Matematica
                    using Mat scientificMat = _converter.RawToMat(fitsData);
                    
                    Cv2.PatchNaNs(scientificMat);       
                    Cv2.Flip(scientificMat, scientificMat, FlipMode.X); 

                    // C. Calcolo Livelli di Contrasto (Stretch)
                    double black, white;

                    if (profile is AbsoluteContrastProfile abs)
                    {
                        // Copia valori assoluti (ADU fissi per tutto il video)
                        black = abs.BlackAdu;
                        white = abs.WhiteAdu;
                    }
                    else if (profile is SigmaContrastProfile sigmaProf)
                    {
                        // Adattamento Dinamico (Sigma-based)
                        // Calcola statistiche per QUESTO frame
                        var (mean, std) = _analysis.ComputeStatistics(scientificMat);
    
                        // Applica K-Sigma
                        black = mean + (sigmaProf.KBlack * std);
                        white = mean + (sigmaProf.KWhite * std);
                    }
                    else
                    {
                        // Fallback
                        black = 0; white = 65535;
                    }

                    // D. Rendering Visuale
                    RenderFrameTo8Bit(scientificMat, frame8Bit, black, white, mode);

                    // E. Scrittura su disco
                    writer.Write(frame8Bit);
                }
            }
            finally
            {
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

        // Zero-Copy Wrapper
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
        if (Math.Abs(range) < 1e-9) range = 1e-5; 

        if (mode == VisualizationMode.Linear)
        {
            double alpha = 255.0 / range;
            double beta = -black * alpha;
            
            if (double.IsInfinity(alpha)) alpha = 0;
            if (double.IsInfinity(beta)) beta = 0;

            src.ConvertTo(dst8Bit, MatType.CV_8UC1, alpha, beta);
        }
        else
        {
            double scale = 1.0 / range;
            double offset = -black * scale;

            using Mat tempMat = new Mat();
            
            src.ConvertTo(tempMat, MatType.CV_32FC1, scale, offset);

            Cv2.Threshold(tempMat, tempMat, 0, 0, ThresholdTypes.Tozero);
            Cv2.Threshold(tempMat, tempMat, 1, 1, ThresholdTypes.Trunc); 

            if (mode == VisualizationMode.SquareRoot)
            {
                Cv2.Sqrt(tempMat, tempMat);
            }
            else if (mode == VisualizationMode.Logarithmic)
            {
                Cv2.Add(tempMat, 1.0, tempMat);
                Cv2.Log(tempMat, tempMat);
                Cv2.Multiply(tempMat, 1.442695, tempMat);
            }

            tempMat.ConvertTo(dst8Bit, MatType.CV_8UC1, 255.0);
        }
    }
}