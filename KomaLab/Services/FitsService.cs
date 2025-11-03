using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Platform;
using KomaLab.Models;
using nom.tam.fits;

namespace KomaLab.Services;

public class FitsService : IFitsService
{
    /// <summary>
    /// Carica un file FITS da un percorso asset, lo parsa e calcola le soglie iniziali.
    /// </summary>
    public async Task<FitsImageData> LoadFitsFromFileAsync(string assetPath)
    {
        // Usa 'await using' per un MemoryStream che conterrà i dati
        await using var memoryStream = new MemoryStream();

        // --- Step 1: Carica i byte in un MemoryStream ---
        if (assetPath.StartsWith("avares://"))
        {
            // È una risorsa asset
            var uri = new Uri(assetPath);
            if (!AssetLoader.Exists(uri))
            {
                throw new FileNotFoundException("Asset non trovato", assetPath);
            }
            await using (var assetStream = AssetLoader.Open(uri))
            {
                await assetStream.CopyToAsync(memoryStream);
            }
        }
        else if (File.Exists(assetPath))
        {
            // È un file del filesystem
            await using (var fileStream = File.OpenRead(assetPath))
            {
                await fileStream.CopyToAsync(memoryStream);
            }
        }
        else
        {
            throw new FileNotFoundException("File non trovato", assetPath);
        }
        
        // Riporta il MemoryStream all'inizio
        memoryStream.Position = 0;

        // --- Step 2: Parsing FITS e Calcolo Soglie (in background) ---
        return await Task.Run(() =>
        {
            // --- ECCO LA LOGICA CORRETTA ---
            // 1. Crea un oggetto Fits
            var fitsFile = new Fits(memoryStream);
            
            // 2. Leggi tutti gli HDU
            fitsFile.Read(); 

            // 3. Cerca il primo HDU che sia un'immagine valida
            ImageHDU? imageHdu = null;
            for (int i = 0; i < fitsFile.NumberOfHDUs; i++)
            {
                var hdu = fitsFile.GetHDU(i);
                if (hdu is ImageHDU hduAsImage && hduAsImage.Data?.Kernel != null)
                {
                    imageHdu = hduAsImage;
                    break; // Trovato!
                }
            }

            if (imageHdu == null)
            {
                throw new NotSupportedException("Il file FITS non contiene un HDU immagine valido.");
            }
            // --- FINE DELLA CORREZIONE ---

            var header = imageHdu.Header;
            int naxis = header.GetIntValue("NAXIS");
            if (naxis < 2) { throw new NotSupportedException($"NAXIS = {naxis}"); }

            int width = header.GetIntValue("NAXIS1");
            int height = header.GetIntValue("NAXIS2");
            var imageSize = new Size(width, height);

            var kernelData = imageHdu.Data.Kernel;
            if (kernelData == null) { throw new NullReferenceException("Kernel è nullo."); }

            var dataArray = (Array)kernelData;
            if (dataArray.Rank != 1) { throw new NotSupportedException($"Rank non 1 (è {dataArray.Rank})"); }

            var rawFitsData = dataArray.Clone(); // Clona l'array jagged

            // --- Step 3: Calcolo Soglie Iniziali (Logica ripristinata) ---
            var (blackPoint, whitePoint) = CalculateClippedThresholds(rawFitsData, header);

            return new FitsImageData
            {
                RawData = rawFitsData,
                FitsHeader = header,
                ImageSize = imageSize,
                InitialBlackPoint = blackPoint,
                InitialWhitePoint = whitePoint
            };
        });
    }

    /// <summary>
    /// Normalizza i dati FITS grezzi in un array di byte (Gray8) 
    /// usando le soglie specificate.
    /// </summary>
    public byte[] NormalizeData(object rawData, Header header, int width, int height, double blackPoint, double whitePoint)
    {
        int bitpix = header.GetIntValue("BITPIX");
        
        // Questa funzione ora contiene lo switch che prima era in NodeViewModel
        switch (bitpix)
        {
            case 8: return Normalize(ConvertJaggedArray<byte>((Array[])rawData), width, height, blackPoint, whitePoint);
            case 16: return Normalize(ConvertJaggedArray<short>((Array[])rawData), width, height, blackPoint, whitePoint);
            case 32: return Normalize(ConvertJaggedArray<int>((Array[])rawData), width, height, blackPoint, whitePoint);
            case -32: return Normalize(ConvertJaggedArray<float>((Array[])rawData), width, height, blackPoint, whitePoint);
            case -64: return Normalize(ConvertJaggedArray<double>((Array[])rawData), width, height, blackPoint, whitePoint);
            default:
                throw new NotSupportedException($"BITPIX non supportato per Normalizzazione: {bitpix}");
        }
    }

    // --- METODI HELPER PRIVATI (Logica di calcolo) ---

    public (double BlackPoint, double WhitePoint) CalculateClippedThresholds(object rawData, Header header)
    {
        int bitpix = header.GetIntValue("BITPIX");

        switch (bitpix)
        {
            case 8: return GetPercentiles(ConvertJaggedArray<byte>((Array[])rawData));
            case 16: return GetPercentiles(ConvertJaggedArray<short>((Array[])rawData));
            case 32: return GetPercentiles(ConvertJaggedArray<int>((Array[])rawData));
            case -32: return GetPercentiles(ConvertJaggedArray<float>((Array[])rawData));
            case -64: return GetPercentiles(ConvertJaggedArray<double>((Array[])rawData));
            default:
                throw new NotSupportedException($"BITPIX non supportato per GetPercentiles: {bitpix}");
        }
    }

    private (double BlackPoint, double WhitePoint) GetPercentiles<T>(T[][] data) where T : struct
    {
        if (data.Length == 0 || data[0].Length == 0) return (0, 255);

        var pixelValues = new List<double>(data.Length * data[0].Length);
        double min = double.MaxValue;
        double max = double.MinValue;

        for (int j = 0; j < data.Length; j++)
        {
            T[] row = data[j];
            for (int i = 0; i < row.Length; i++)
            {
                double val = Convert.ToDouble(row[i]);
                if (double.IsNaN(val) || double.IsInfinity(val)) continue;
                pixelValues.Add(val);
                if (val < min) min = val;
                if (val > max) max = val;
            }
        }

        if (pixelValues.Count == 0) return (0, 255);
        pixelValues.Sort();

        double blackClipPercent = 0.02;
        double whiteClipPercent = 0.998;

        int blackIndex = (int)(pixelValues.Count * blackClipPercent);
        int whiteIndex = (int)(pixelValues.Count * whiteClipPercent) - 1;

        if (whiteIndex < 0) whiteIndex = 0;
        if (whiteIndex >= pixelValues.Count) whiteIndex = pixelValues.Count - 1;
        if (blackIndex >= whiteIndex) blackIndex = 0;

        double blackPoint = pixelValues[blackIndex];
        double whitePoint = pixelValues[whiteIndex];

        if (whitePoint <= blackPoint)
        {
            blackPoint = min;
            whitePoint = max;
        }
        return (blackPoint, whitePoint);
    }

    private T[][] ConvertJaggedArray<T>(Array[] source) where T : struct
    {
        T[][] result = new T[source.Length][];
        for (int i = 0; i < source.Length; i++)
        {
            result[i] = (T[])source[i];
        }
        return result;
    }

    private byte[] Normalize<T>(T[][] data, int width, int height, double blackPoint, double whitePoint) where T : struct
    {
        byte[] outputBytes = new byte[width * height];
        double range = whitePoint - blackPoint;
        
        // Gestione divisione per zero o range negativo
        if (range <= 0)
        {
            byte fillValue = (byte)(blackPoint >= whitePoint ? 0 : 128);
            Array.Fill(outputBytes, fillValue);
            return outputBytes;
        }

        int k = 0;
        for (int j = height - 1; j >= 0; j--) // Inverti per orientamento FITS standard
        {
            T[] row = data[j];
            for (int i = 0; i < width; i++)
            {
                double val = Convert.ToDouble(row[i]);
                
                // Clipping
                if (val <= blackPoint) { outputBytes[k] = 0; }
                else if (val >= whitePoint) { outputBytes[k] = 255; }
                else 
                { 
                    // Normalizzazione
                    double normalized = (val - blackPoint) / range; 
                    outputBytes[k] = (byte)(normalized * 255.0); 
                }
                k++;
            }
        }
        return outputBytes;
    }
}