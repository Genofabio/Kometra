using System;
using System.Collections;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Platform; 
using KomaLab.Models;
using nom.tam.fits;
using nom.tam.util;
using OpenCvSharp;

namespace KomaLab.Services;

public class FitsService : IFitsService
{
    /// <summary>
    /// Carica un file FITS (sia da asset che da filesystem), lo parsa 
    /// e restituisce il modello dati popolato.
    /// </summary>
    public async Task<FitsImageData?> LoadFitsFromFileAsync(string assetPath)
    {
        // Apre lo stream (gestisce sia file su disco che risorse embedded)
        Stream streamToRead = OpenStream(assetPath);

        // 'await using' assicura la chiusura dello stream alla fine
        await using (streamToRead)
        {
            // Esegue il parsing pesante in background per non bloccare la UI
            return await Task.Run(() =>
            {
                // Inizializza il lettore FITS
                var fitsFile = new Fits(streamToRead);
                fitsFile.Read(); 

                ImageHDU? imageHdu = null;
                
                // Cerca la prima HDU che contiene un'immagine valida (almeno 2 assi)
                for (int i = 0; i < fitsFile.NumberOfHDUs; i++)
                {
                    var hdu = fitsFile.GetHDU(i);
                    if (hdu is ImageHDU imgHdu && imgHdu.Axes.Length >= 2)
                    {
                        imageHdu = imgHdu;
                        break;
                    }
                }

                // Se non troviamo immagini valide, ritorniamo null
                if (imageHdu == null) return null;

                var header = imageHdu.Header;
                
                // Lettura dimensioni
                int width = header.GetIntValue("NAXIS1");
                int height = header.GetIntValue("NAXIS2");
                
                // Estrazione dei dati grezzi (Kernel restituisce l'array sottostante)
                // CSharpFITS solitamente restituisce jagged arrays (es. short[][]) o array rettangolari.
                var rawData = imageHdu.Kernel; 
                
                if (rawData == null) return null;

                // --- GESTIONE ORIENTAMENTO FITS ---
                // Lo standard FITS ha l'origine in basso a sinistra (Bottom-Left).
                // I monitor e le bitmap hanno l'origine in alto a sinistra (Top-Left).
                // Dobbiamo invertire l'ordine delle righe (Flip Y).
                if (rawData is Array arr && arr.Rank == 1) 
                {
                    // Se è un jagged array (array di array), Array.Reverse inverte l'ordine delle righe.
                    // Questo è molto efficiente e corregge l'orientamento.
                    Array.Reverse(arr);
                }

                // Costruzione del Model
                return new FitsImageData
                {
                    RawData = rawData,
                    FitsHeader = header,
                    Width = width,
                    Height = height
                };
            });
        }
    }
    
    /// <summary>
    /// Legge solo l'header del file FITS per ottenerne le dimensioni,
    /// senza caricare l'intera matrice dati in memoria.
    /// </summary>
    public async Task<(int Width, int Height)> GetFitsImageSizeAsync(string path)
    {
        Stream streamToRead = OpenStream(path);

        await using (streamToRead)
        {
            return await Task.Run(() =>
            {
                var fitsFile = new Fits(streamToRead);
                // Legge (parzialmente se supportato, altrimenti legge tutto lo stream ma parsa solo l'header)
                fitsFile.Read(); 
    
                for (int i = 0; i < fitsFile.NumberOfHDUs; i++)
                {
                    var hdu = fitsFile.GetHDU(i);
                    if (hdu is ImageHDU imgHdu)
                    {
                        return (imgHdu.Header.GetIntValue("NAXIS1"), 
                                imgHdu.Header.GetIntValue("NAXIS2"));
                    }
                }
                return (0, 0);
            });
        }
    }

    /// <summary>
    /// Normalizza una Matrice OpenCV (contenente dati scientifici double/float)
    /// direttamente nella memoria video di una Bitmap 8-bit (Gray8).
    /// </summary>
    public void NormalizeData(
        Mat sourceMat, 
        int width, 
        int height,
        double blackPoint, 
        double whitePoint,
        IntPtr destinationBuffer, 
        long stride)
    {
        if (sourceMat.Empty()) return; 

        // Calcolo parametri Stretch Lineare: y = alpha * x + beta
        // Vogliamo mappare [blackPoint, whitePoint] -> [0, 255]
        double range = whitePoint - blackPoint;
        
        // Evitiamo divisioni per zero se black == white
        double alpha = (Math.Abs(range) < 1e-9) ? 0 : 255.0 / range;
        
        // Calcolo offset (beta)
        double beta = (Math.Abs(range) < 1e-9) 
            ? (blackPoint >= whitePoint ? 0 : 128) 
            : -blackPoint * alpha;

        // Creiamo una "Matrice Wrapper" attorno al buffer di memoria della Bitmap di destinazione.
        // Non alloca nuova memoria, usa quella passata tramite destinationBuffer.
        using Mat dstMat = Mat.FromPixelData(
            height, 
            width, 
            MatType.CV_8UC1, 
            destinationBuffer, 
            stride);

        // ConvertTo esegue: dst(x,y) = saturate_cast<uchar>( src(x,y)*alpha + beta )
        // È un'operazione altamente ottimizzata in OpenCV (spesso SIMD/Multithreaded).
        sourceMat.ConvertTo(dstMat, MatType.CV_8UC1, alpha, beta);
    }
    
    /// <summary>
    /// Salva i dati correnti su disco in formato FITS.
    /// Gestisce il re-flip dei dati e la pulizia dell'header.
    /// </summary>
    public async Task SaveFitsFileAsync(FitsImageData data, string destinationPath)
    {
        await Task.Run(() =>
        {
            // 1. PREPARAZIONE DATI
            if (data.RawData is Array originalSource)
            {
                var arrayToSave = (Array)originalSource.Clone();
                Array.Reverse(arrayToSave);

                var hdu = FitsFactory.HDUFactory(arrayToSave);
                var newHeader = hdu.Header;
            
                var cursor = data.FitsHeader.GetCursor();
                while (cursor.MoveNext())
                {
                    HeaderCard? card = null;

                    if (cursor.Current is DictionaryEntry entry && entry.Value is HeaderCard hc)
                        card = hc;
                    else if (cursor.Current is HeaderCard c)
                        card = c;

                    if (card == null) continue;

                    string key = card.Key.ToUpper();

                    if (key == "SIMPLE" || key == "BITPIX" || key == "EXTEND" ||
                        key == "PCOUNT" || key == "GCOUNT" || 
                        key == "BZERO" || key == "BSCALE" ||
                        key == "DATAMIN" || key == "DATAMAX" ||
                        key.StartsWith("NAXIS")) 
                    {
                        continue; 
                    }

                    newHeader.AddCard(card);
                }

                // 4. SCRITTURA SU FILE
                // --- CORREZIONE QUI SOTTO: Usa ReadWrite ---
                using var fs = new FileStream(destinationPath, FileMode.Create, FileAccess.ReadWrite);
            
                // BufferedDataStream richiede che lo stream sottostante sia leggibile anche in scrittura
                using var bs = new BufferedDataStream(fs);
                var fitsFile = new Fits();
            
                fitsFile.AddHDU(hdu);
                fitsFile.Write(bs);
            
                bs.Flush();
                fs.Flush();
            }
        });
    }

    // --- Helper Privato ---
    
    private Stream OpenStream(string path)
    {
        // Gestione Risorse Avalonia (es. immagini di default o demo)
        if (path.StartsWith("avares://"))
        {
            var uri = new Uri(path);
            if (!AssetLoader.Exists(uri)) 
                throw new FileNotFoundException($"Asset Avalonia non trovato: {path}");
            
            return AssetLoader.Open(uri);
        }
        
        // Gestione File System Standard
        if (File.Exists(path))
        {
            // FileShare.Read permette ad altre app di leggere il file mentre lo apriamo
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }

        throw new FileNotFoundException($"File non trovato sul disco: {path}");
    }
}