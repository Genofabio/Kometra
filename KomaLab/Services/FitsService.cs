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
                int naxis = header.GetIntValue("NAXIS");
                if (naxis < 2) return null;

                int width = header.GetIntValue("NAXIS1");
                int height = header.GetIntValue("NAXIS2");
                var imageSize = new Size(width, height);
                
                var kernelData = imageHdu.Data.Kernel; 
                if (kernelData == null) return null; 

                var dataArray = (Array)kernelData;
                if (dataArray.Rank != 1) return null;
                
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
    public void NormalizeData(object rawData, Header header, int width, int height,
        double blackPoint, double whitePoint,
        IntPtr destinationBuffer, long stride)
    {
        // --- 2. MODIFICA NormalizeData ---
        using Mat srcMat = _processingService.LoadFitsDataAsMat(
            new FitsImageData 
            { 
                RawData = rawData, 
                FitsHeader = header, 
                ImageSize = new Size(width, height) 
            }
        );

        if (srcMat.Empty()) return; 
    
        double range = whitePoint - blackPoint;
        double alpha = (range <= 0) ? 0 : 255.0 / range;
        double beta = (range <= 0) ? (blackPoint >= whitePoint ? 0 : 128) : -blackPoint * alpha;

        using Mat dstMat = Mat.FromPixelData(
            height, 
            width, 
            MatType.CV_8UC1, 
            destinationBuffer, 
            stride);

        srcMat.ConvertTo(dstMat, MatType.CV_8UC1, alpha, beta);
        Cv2.Flip(dstMat, dstMat, FlipMode.X); 
    }
    
}