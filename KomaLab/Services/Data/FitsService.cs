using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia.Platform;
using KomaLab.Models;
using nom.tam.fits;
using nom.tam.util;
using OpenCvSharp;

namespace KomaLab.Services.Data;

public class FitsService : IFitsService
{
    // --- 1. CARICAMENTO (Refactoring DRY) ---

    public async Task<FitsImageData?> LoadFitsFromFileAsync(string assetPath)
    {
        return await Task.Run(() =>
        {
            // Usiamo l'helper interno per leggere i dati grezzi
            var rawResult = ReadRawFitsData(assetPath);
            if (rawResult == null) return null;

            var (rawData, header, width, height) = rawResult.Value;

            // --- GESTIONE ORIENTAMENTO FITS (Solo per UI/Array C#) ---
            // FITS è Bottom-Left. Per Avalonia/Bitmap serve Top-Left.
            // Invertiamo l'array qui per l'uso in UI.
            // (Nota: Per OpenCV nel video gestiremo il flip separatamente per velocità)
            if (rawData is Array arr && arr.Rank == 1) 
            {
                Array.Reverse(arr);
            }

            return new FitsImageData
            {
                RawData = rawData,
                FitsHeader = header,
                Width = width,
                Height = height
            };
        });
    }

    /// <summary>
    /// Helper privato DRY: Apre il file, trova l'immagine e restituisce i dati grezzi.
    /// Non inverte ancora l'array (Flip Y) per lasciare flessibilità.
    /// </summary>
    private (Array Data, Header Header, int Width, int Height)? ReadRawFitsData(string path)
    {
        try 
        {
            using Stream stream = OpenStream(path);
            var fitsFile = new Fits(stream);
            fitsFile.Read();

            ImageHDU? imageHdu = null;
            
            for (int i = 0; i < fitsFile.NumberOfHDUs; i++)
            {
                var hdu = fitsFile.GetHDU(i);
                if (hdu is ImageHDU imgHdu && imgHdu.Axes.Length >= 2)
                {
                    imageHdu = imgHdu;
                    break;
                }
            }

            if (imageHdu == null) return null;

            var header = imageHdu.Header;
            int width = header.GetIntValue("NAXIS1");
            int height = header.GetIntValue("NAXIS2");
            var rawData = imageHdu.Kernel; // Array jagged o rettangolare

            if (rawData is Array arr)
            {
                return (arr, header, width, height);
            }
            return null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Errore lettura FITS {path}: {ex.Message}");
            return null;
        }
    }

    // --- 2. ESPORTAZIONE VIDEO (Nuova Feature) ---

    public async Task ExportVideoAsync(
        List<string> sourceFiles, 
        string outputPath, 
        double fps,
        ContrastProfile profile)
    {
        await Task.Run(() =>
        {
            if (sourceFiles.Count == 0) return;

            VideoWriter? writer = null;
            Mat? frame8Bit = null; // Buffer riutilizzabile per il frame finale

            try
            {
                // 1. Setup Video Writer (usando dimensioni del primo file)
                var firstData = ReadRawFitsData(sourceFiles[0]);
                if (firstData == null) throw new Exception("Impossibile leggere il primo file.");

                var size = new Size(firstData.Value.Width, firstData.Value.Height);
                
                // Codec MJPG = .avi (Alta compatibilità, qualità buona, file medio-grandi)
                // Se vuoi MP4 compresso, usa FourCC('H','2','6','4') ma richiede openh264.dll o ffmpeg
                int fourcc = VideoWriter.FourCC('M', 'J', 'P', 'G');
                
                writer = new VideoWriter(outputPath, fourcc, fps, size, isColor: false); // isColor=false per B/N

                if (!writer.IsOpened())
                    throw new Exception("Impossibile inizializzare il VideoWriter.");

                // Alloca buffer per la conversione finale (Gray8)
                frame8Bit = new Mat(size.Height, size.Width, MatType.CV_8UC1);

                // 2. Loop di elaborazione frame
                foreach (string path in sourceFiles)
                {
                    var dataTuple = ReadRawFitsData(path);
                    if (dataTuple == null) continue;

                    var (rawData, _, w, h) = dataTuple.Value;

                    // Validazione dimensioni (devono essere tutte uguali per il video)
                    if (w != size.Width || h != size.Height) continue;

                    // 2a. Conversione Raw Array -> Matrice OpenCV Scientifica (Double/Float/Short)
                    using Mat scientificMat = RawArrayToMat(rawData, w, h);
                    if (scientificMat.Empty()) continue;

                    // 2b. Flip Y (FITS -> Video Convention)
                    // FITS è origine in basso-sx, Video in alto-sx.
                    Cv2.Flip(scientificMat, scientificMat, FlipMode.X);

                    // 2c. Calcolo Soglie (Black/White Point)
                    double black = 0, white = 1;

                    if (profile.IsAbsolute)
                    {
                        black = profile.Black;
                        white = profile.White;
                    }
                    else
                    {
                        // Calcolo Statistiche (Sigma Clipping dinamico per ogni frame)
                        Cv2.MeanStdDev(scientificMat, out var meanScalar, out var stdDevScalar);
                        double mean = meanScalar.Val0;
                        double sigma = stdDevScalar.Val0;

                        black = mean + (profile.KBlack * sigma);
                        white = mean + (profile.KWhite * sigma);
                    }

                    // 2d. Normalizzazione (Auto-Stretch) e scrittura nel buffer 8-bit
                    // Formula: dst = (src * alpha) + beta
                    double range = white - black;
                    double alpha = (Math.Abs(range) < 1e-9) ? 0 : 255.0 / range;
                    double beta = (Math.Abs(range) < 1e-9) 
                        ? (black >= white ? 0 : 128) 
                        : -black * alpha;

                    scientificMat.ConvertTo(frame8Bit, MatType.CV_8UC1, alpha, beta);

                    // 2e. Scrittura Frame
                    writer.Write(frame8Bit);
                    
                    // Il 'using scientificMat' libera la memoria raw di questo frame subito
                }
            }
            finally
            {
                writer?.Dispose();
                frame8Bit?.Dispose();
            }
        });
    }
    
    /// <summary>
    /// Converte un array C# generico in una Mat OpenCV usando Marshal.Copy.
    /// È molto più veloce di SetArray ed evita errori di ambiguità sui tipi.
    /// </summary>
    private Mat RawArrayToMat(Array rawData, int width, int height)
    {
        // Caso 1: Array 1D (Appiattito)
        if (rawData.Rank == 1)
        {
            // 1. Short (16-bit signed)
            if (rawData is short[] sData)
            {
                var mat = new Mat(height, width, MatType.CV_16SC1);
                Marshal.Copy(sData, 0, mat.Data, sData.Length);
                return mat;
            }
            // 2. Float (32-bit float)
            if (rawData is float[] fData)
            {
                var mat = new Mat(height, width, MatType.CV_32FC1);
                Marshal.Copy(fData, 0, mat.Data, fData.Length);
                return mat;
            }
            // 3. Double (64-bit float)
            if (rawData is double[] dData)
            {
                var mat = new Mat(height, width, MatType.CV_64FC1);
                Marshal.Copy(dData, 0, mat.Data, dData.Length);
                return mat;
            }
            // 4. Int (32-bit signed)
            if (rawData is int[] iData)
            {
                var mat = new Mat(height, width, MatType.CV_32SC1);
                Marshal.Copy(iData, 0, mat.Data, iData.Length);
                return mat;
            }
            // 5. Byte (8-bit unsigned)
            if (rawData is byte[] bData)
            {
                var mat = new Mat(height, width, MatType.CV_8UC1);
                Marshal.Copy(bData, 0, mat.Data, bData.Length);
                return mat;
            }
        }
        
        // Caso 2: Jagged Array (short[][]) - Tipico di CSharpFITS
        if (rawData is IEnumerable enumerable)
        {
            Mat? mat = null;
            int row = 0;

            foreach (var item in enumerable)
            {
                if (item is Array rowArray)
                {
                    // Inizializzazione Lazy al primo giro
                    if (mat == null)
                    {
                        MatType type = MatType.CV_64FC1; // Default
                        if (rowArray is short[]) type = MatType.CV_16SC1;
                        else if (rowArray is float[]) type = MatType.CV_32FC1;
                        else if (rowArray is double[]) type = MatType.CV_64FC1;
                        else if (rowArray is int[]) type = MatType.CV_32SC1;
                        else if (rowArray is byte[]) type = MatType.CV_8UC1;

                        mat = new Mat(height, width, type);
                    }

                    if (row < height)
                    {
                        // Ottieniamo il puntatore all'inizio della riga corrente nella Matrice OpenCV
                        IntPtr rowPtr = mat.Ptr(row);

                        // Copiamo la riga C# direttamente in quella posizione di memoria
                        if (rowArray is short[] s) Marshal.Copy(s, 0, rowPtr, s.Length);
                        else if (rowArray is float[] f) Marshal.Copy(f, 0, rowPtr, f.Length);
                        else if (rowArray is double[] d) Marshal.Copy(d, 0, rowPtr, d.Length);
                        else if (rowArray is int[] i) Marshal.Copy(i, 0, rowPtr, i.Length);
                        else if (rowArray is byte[] b) Marshal.Copy(b, 0, rowPtr, b.Length);
                    }
                    row++;
                }
            }
            return mat ?? new Mat();
        }

        return new Mat();
    }

    // --- 3. METODI ESISTENTI (GetSize, Save, Normalize) ---

    public async Task<(int Width, int Height)> GetFitsImageSizeAsync(string path)
    {
        Stream streamToRead = OpenStream(path);
        await using (streamToRead)
        {
            return await Task.Run(() =>
            {
                var fitsFile = new Fits(streamToRead);
                fitsFile.Read(); 
                for (int i = 0; i < fitsFile.NumberOfHDUs; i++)
                {
                    if (fitsFile.GetHDU(i) is ImageHDU imgHdu)
                        return (imgHdu.Header.GetIntValue("NAXIS1"), imgHdu.Header.GetIntValue("NAXIS2"));
                }
                return (0, 0);
            });
        }
    }

    public void NormalizeData(Mat sourceMat, int width, int height, double blackPoint, double whitePoint, IntPtr destinationBuffer, long stride)
    {
        if (sourceMat.Empty()) return; 
        double range = whitePoint - blackPoint;
        double alpha = (Math.Abs(range) < 1e-9) ? 0 : 255.0 / range;
        double beta = (Math.Abs(range) < 1e-9) ? (blackPoint >= whitePoint ? 0 : 128) : -blackPoint * alpha;

        using Mat dstMat = Mat.FromPixelData(height, width, MatType.CV_8UC1, destinationBuffer, stride);
        sourceMat.ConvertTo(dstMat, MatType.CV_8UC1, alpha, beta);
    }
    
    public async Task SaveFitsFileAsync(FitsImageData data, string destinationPath)
    {
        await Task.Run(() =>
        {
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
                    if (cursor.Current is DictionaryEntry entry && entry.Value is HeaderCard hc) card = hc;
                    else if (cursor.Current is HeaderCard c) card = c;

                    if (card == null) continue;
                    string key = card.Key.ToUpper();
                    if (key == "SIMPLE" || key == "BITPIX" || key == "EXTEND" || key.StartsWith("NAXIS") || key == "PCOUNT" || key == "GCOUNT") continue; 
                    newHeader.AddCard(card);
                }

                using var fs = new FileStream(destinationPath, FileMode.Create, FileAccess.ReadWrite);
                using var bs = new BufferedDataStream(fs);
                var fitsFile = new Fits();
                fitsFile.AddHDU(hdu);
                fitsFile.Write(bs);
                bs.Flush();
                fs.Flush();
            }
        });
    }

    private Stream OpenStream(string path)
    {
        if (path.StartsWith("avares://"))
        {
            var uri = new Uri(path);
            if (!AssetLoader.Exists(uri)) throw new FileNotFoundException($"Asset non trovato: {path}");
            return AssetLoader.Open(uri);
        }
        if (File.Exists(path)) return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        throw new FileNotFoundException($"File non trovato: {path}");
    }
    
    // --- 4. ORDINAMENTO TEMPORALE (Metadata Scan) ---

    public async Task<List<string>> SortFilesByObservationTimeAsync(List<string> filePaths)
    {
        // Se c'è 0 o 1 file, è già ordinato
        if (filePaths.Count <= 1) return filePaths;

        return await Task.Run(() =>
        {
            // Usiamo una lista thread-safe per raccogliere i risultati
            var fileInfos = new System.Collections.Concurrent.ConcurrentBag<(string Path, DateTime? Date)>();

            // Scansione parallela degli header (molto più veloce del sequenziale per I/O)
            Parallel.ForEach(filePaths, (path) =>
            {
                var date = TryGetObservationDate(path);
                fileInfos.Add((path, date));
            });

            // LOGICA DI ORDINAMENTO:
            // 1. I file con data valida vengono prima.
            // 2. Ordina cronologicamente (dal più vecchio al più recente).
            // 3. Se la data è uguale o manca, usa il nome del file come fallback.
            
            var sortedList = fileInfos
                .OrderBy(x => x.Date.HasValue ? 0 : 1) // Priorità a chi ha la data
                .ThenBy(x => x.Date)                   // Cronologico
                .ThenBy(x => x.Path)                   // Alfabetico (Fallback)
                .Select(x => x.Path)
                .ToList();

            return sortedList;
        });
    }

    /// <summary>
    /// Legge SOLO l'header del FITS senza caricare i dati immagine.
    /// Cerca DATE-OBS (Standard) o DATE.
    /// </summary>
    private DateTime? TryGetObservationDate(string path)
    {
        try
        {
            using Stream stream = OpenStream(path);
            var fitsFile = new Fits(stream);
            
            // ReadHDU legge solo l'header iniziale e si ferma prima dei dati.
            // È l'operazione più leggera possibile.
            BasicHDU? hdu = fitsFile.ReadHDU();
            
            if (hdu == null) return null;

            Header header = hdu.Header;
            
            // DATE-OBS è lo standard IAU per "Data e ora dell'osservazione"
            string? dateStr = header.GetStringValue("DATE-OBS");
            
            // Fallback: Alcuni software usano solo "DATE" (che a volte è creazione file, ma meglio di nulla)
            if (string.IsNullOrEmpty(dateStr)) 
            {
                dateStr = header.GetStringValue("DATE");
            }

            // Tentiamo il parsing
            if (!string.IsNullOrEmpty(dateStr) && DateTime.TryParse(dateStr, out DateTime dt))
            {
                return dt;
            }

            return null;
        }
        catch 
        {
            // Se il file è illeggibile o non è un FITS valido, lo mettiamo in fondo
            return null;
        }
    }
}