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
// VERSIONE: Aggiornata per architettura No-FitsImageData
// ---------------------------------------------------------------------------

public class MediaExportService : IMediaExportService
{
    private readonly IFitsIoService _ioService;
    private readonly IFitsOpenCvConverter _converter;
    private readonly IImageAnalysisService _analysis; 

    public MediaExportService(
        IFitsIoService ioService, 
        IFitsOpenCvConverter converter,
        IImageAnalysisService analysis) 
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
                // 1. Inizializzazione VideoWriter (Leggiamo solo l'header del primo file per le dimensioni)
                var firstHeader = await _ioService.ReadHeaderAsync(sourceFiles[0]);
                if (firstHeader == null) throw new IOException($"Impossibile leggere header del primo file: {sourceFiles[0]}");

                int width = firstHeader.GetIntValue("NAXIS1");
                int height = firstHeader.GetIntValue("NAXIS2");
                var size = new Size(width, height);

                int fourcc = VideoWriter.FourCC('M', 'J', 'P', 'G'); 
                
                writer = new VideoWriter(outputFilePath, fourcc, fps, size, isColor: false);
                
                if (!writer.IsOpened()) 
                    throw new IOException("Errore inizializzazione VideoWriter OpenCV.");

                frame8Bit = new Mat(size.Height, size.Width, MatType.CV_8UC1);

                // 2. Loop di elaborazione frame
                foreach (string path in sourceFiles)
                {
                    // A. Caricamento Separato (Header + Pixel)
                    var header = await _ioService.ReadHeaderAsync(path);
                    var rawPixels = await _ioService.ReadPixelDataAsync(path);

                    if (header == null || rawPixels == null) continue;
                    
                    // Verifica dimensioni (tramite array per sicurezza)
                    if (rawPixels.GetLength(1) != width || rawPixels.GetLength(0) != height) continue;

                    // B. Estrazione Scaling dall'Header
                    double bScale = header.GetValue<double>("BSCALE") ?? 1.0;
                    double bZero = header.GetValue<double>("BZERO") ?? 0.0;

                    // C. Conversione in Matrice Matematica (già flippata correttamente dal loader)
                    using Mat scientificMat = _converter.RawToMat(rawPixels, bScale, bZero);
                    
                    Cv2.PatchNaNs(scientificMat);
                    
                    // NOTA: Rimossa Cv2.Flip. FitsIoService fornisce già l'array orientato Top-Left.

                    // D. Calcolo Livelli di Contrasto (Stretch)
                    double black, white;

                    if (profile is AbsoluteContrastProfile abs)
                    {
                        black = abs.BlackAdu;
                        white = abs.WhiteAdu;
                    }
                    else if (profile is SigmaContrastProfile sigmaProf)
                    {
                        var (mean, std) = _analysis.ComputeStatistics(scientificMat);
                        black = mean + (sigmaProf.KBlack * std);
                        white = mean + (sigmaProf.KWhite * std);
                    }
                    else
                    {
                        black = 0; white = 65535;
                    }

                    // E. Rendering Visuale
                    RenderFrameTo8Bit(scientificMat, frame8Bit, black, white, mode);

                    // F. Scrittura su disco
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