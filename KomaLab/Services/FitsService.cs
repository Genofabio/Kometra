using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Platform;
using KomaLab.Models;
using MathNet.Numerics.Statistics;
using nom.tam.fits;
using OpenCvSharp;
using Size = Avalonia.Size;

namespace KomaLab.Services;

public class FitsService : IFitsService
{
    private readonly IImageProcessingService _processingService;

    public FitsService(IImageProcessingService processingService)
    {
        _processingService = processingService;
    }
    
    /// <summary>
    /// Carica un file FITS (sia da asset che da filesystem), lo parsa e calcola le soglie.
    /// </summary>
    public async Task<FitsImageData?> LoadFitsFromFileAsync(string assetPath)
    {
        // 1. Apri lo stream corretto (File o Asset)
        Stream streamToRead;
        if (assetPath.StartsWith("avares://"))
        {
            var uri = new Uri(assetPath);
            if (!AssetLoader.Exists(uri)) throw new FileNotFoundException("Asset non trovato", assetPath);
            streamToRead = AssetLoader.Open(uri);
        }
        else if (File.Exists(assetPath))
        {
            streamToRead = new FileStream(assetPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        else
        {
            throw new FileNotFoundException("File non trovato", assetPath);
        }

        // 2. 'await using' si assicurerà che lo stream venga chiuso
        await using (streamToRead)
        {
            // 3. Esegui il parsing "pesante" in background
            return await Task.Run(() =>
            {
                // 4. Passa lo stream DIRETTO.
                var fitsFile = new Fits(streamToRead);
                fitsFile.Read(); 

                ImageHDU? imageHdu = null;
                for (int i = 0; i < fitsFile.NumberOfHDUs; i++)
                {
                    var hdu = fitsFile.GetHDU(i);
                    if (hdu is ImageHDU hduAsImage && hduAsImage.Data?.Kernel != null)
                    {
                        imageHdu = hduAsImage;
                        break;
                    }
                }

                if (imageHdu == null)
                {
                    return null; 
                }

                var header = imageHdu.Header;
                
                // --- PUNTO DI ISPEZIONE DEBUG ---
                double bzero = header.GetDoubleValue("BZERO", 0.0);
                double bscale = header.GetDoubleValue("BSCALE", 1.0);
                int bitpix = header.GetIntValue("BITPIX");
            
                System.Diagnostics.Debug.WriteLine($"--------------------------------------------------");
                System.Diagnostics.Debug.WriteLine($"[FITS LOAD] File: {Path.GetFileName(assetPath)}");
                System.Diagnostics.Debug.WriteLine($"[FITS LOAD] BITPIX: {bitpix}");
                System.Diagnostics.Debug.WriteLine($"[FITS LOAD] BZERO : {bzero}"); // Se questo è != 0, è lui la causa
                System.Diagnostics.Debug.WriteLine($"[FITS LOAD] BSCALE: {bscale}");
                System.Diagnostics.Debug.WriteLine($"--------------------------------------------------");
                // --------------------------------
                
                int naxis = header.GetIntValue("NAXIS");
                if (naxis < 2) return null;

                int width = header.GetIntValue("NAXIS1");
                int height = header.GetIntValue("NAXIS2");
                var imageSize = new Size(width, height);
                
                var kernelData = imageHdu.Data.Kernel; 
                if (kernelData == null) return null; 

                var dataArray = (Array)kernelData;
                if (dataArray.Rank != 1) return null;
                Array.Reverse(dataArray);
                
                var rawFitsData = dataArray.Clone(); 

                return new FitsImageData
                {
                    RawData = rawFitsData,
                    FitsHeader = header,
                    ImageSize = imageSize
                };
            });
        }
    }
    
    /// <summary>
    /// Legge solo le dimensioni dei file FITS.
    /// </summary>
    public async Task<Size> GetFitsImageSizeAsync(string path)
    {
        // 1. Apri lo stream corretto (File o Asset)
        Stream streamToRead;
        if (path.StartsWith("avares://"))
        {
            var uri = new Uri(path);
            if (!AssetLoader.Exists(uri)) throw new FileNotFoundException("Asset non trovato", path);
            streamToRead = AssetLoader.Open(uri);
        }
        else if (File.Exists(path))
        {
            streamToRead = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        else
        {
            throw new FileNotFoundException("File non trovato", path);
        }

        // 2. 'await using' si assicurerà che lo stream venga chiuso
        await using (streamToRead)
        {
            // 3. Esegui la lettura "leggera" in background
            return await Task.Run(() =>
            {
                // 4. Passa lo stream (FileStream/AssetStream) DIRETTAMENTE.
                var fitsFile = new Fits(streamToRead);
                fitsFile.Read(); 
    
                for (int i = 0; i < fitsFile.NumberOfHDUs; i++)
                {
                    var hdu = fitsFile.GetHDU(i);
                    if (hdu is ImageHDU hduAsImage)
                    {
                        var header = hduAsImage.Header;
                        int width = header.GetIntValue("NAXIS1");
                        int height = header.GetIntValue("NAXIS2");
                        return new Size(width, height);
                    }
                }
        
                return default(Size);
            });
        }
    }

    /// <summary>
    /// Normalizza i dati FITS grezzi in un array di byte (Gray8) 
    /// usando le soglie specificate.
    /// </summary>
    public void NormalizeData(
        OpenCvSharp.Mat sourceMat, // Matrice CACHED (CV_64FC1)
        int width, 
        int height,
        double blackPoint, 
        double whitePoint,
        IntPtr destinationBuffer, 
        long stride)
    {
        // Il check su BZERO/BSCALE non è più necessario qui, perché è stato fatto 
        // una sola volta in FitsRenderer.InitializeAsync.

        if (sourceMat.Empty()) return; 

        // Calcolo Alpha e Beta per lo Stretch Lineare
        double range = whitePoint - blackPoint;
        double alpha = (Math.Abs(range) < 1e-9) ? 0 : 255.0 / range;
        double beta = (Math.Abs(range) < 1e-9) ? (blackPoint >= whitePoint ? 0 : 128) : -blackPoint * alpha;

        // 1. Creiamo il wrapper sulla memoria video di destinazione (dstMat)
        using Mat dstMat = Mat.FromPixelData(
            height, 
            width, 
            MatType.CV_8UC1, 
            destinationBuffer, 
            stride);

        // 2. Eseguiamo la conversione e lo stretch (Operazione Veloce OpenCV)
        // Usiamo la Matrice CACHED (sourceMat)
        sourceMat.ConvertTo(dstMat, MatType.CV_8UC1, alpha, beta);
    }
    
}