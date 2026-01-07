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
    // --- 1. CARICAMENTO (Con Buffer in RAM) ---

    public async Task<FitsImageData?> LoadFitsFromFileAsync(string assetPath)
    {
        return await Task.Run(() =>
        {
            var rawResult = ReadRawFitsData(assetPath);
            if (rawResult == null) return null;

            var (rawData, header, width, height) = rawResult.Value;

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

    private (Array Data, Header Header, int Width, int Height)? ReadRawFitsData(string path)
    {
        try 
        {
            // Copia in RAM e rilascio immediato del file per evitare lock
            using Stream fileStream = OpenStream(path);
            using var memoryStream = new MemoryStream();
            
            fileStream.CopyTo(memoryStream);
            memoryStream.Position = 0; 
            
            var fitsFile = new Fits(memoryStream);
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
            var rawData = imageHdu.Kernel;

            if (rawData is Array arr)
            {
                return (arr, header, width, height);
            }
            return null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[FitsService] Errore lettura FITS {path}: {ex.Message}");
            return null;
        }
    }

    // --- 2. ESPORTAZIONE VIDEO (WYSIWYG Reale) ---

    // --- 2. ESPORTAZIONE VIDEO (DEBUG VERSION) ---
public async Task ExportVideoAsync(
        List<string> sourceFiles, 
        string outputPath, 
        double fps,
        ContrastProfile profile,
        VisualizationMode mode = VisualizationMode.Linear)
    {
        await Task.Run(() =>
        {
            if (sourceFiles.Count == 0) return;

            VideoWriter? writer = null;
            Mat? frame8Bit = null; 
            // Maschera riutilizzabile per escludere i bordi neri
            using Mat mask = new Mat(); 

            try
            {
                // Init VideoWriter
                var firstData = ReadRawFitsData(sourceFiles[0]);
                if (firstData == null) throw new Exception("Impossibile leggere il primo file.");
                var size = new Size(firstData.Value.Width, firstData.Value.Height);
                
                int fourcc = VideoWriter.FourCC('M', 'J', 'P', 'G');
                writer = new VideoWriter(outputPath, fourcc, fps, size, isColor: false);
                
                if (!writer.IsOpened()) throw new Exception("Impossibile aprire VideoWriter.");

                frame8Bit = new Mat(size.Height, size.Width, MatType.CV_8UC1);

                foreach (string path in sourceFiles)
                {
                    var dataTuple = ReadRawFitsData(path);
                    if (dataTuple == null) continue;

                    var (rawData, _, w, h) = dataTuple.Value;
                    if (w != size.Width || h != size.Height) continue;

                    // 1. Carichiamo i dati grezzi (potrebbero essere Double/CV_64F)
                    using Mat tempSourceMat = RawArrayToMat(rawData, w, h);
                    if (tempSourceMat.Empty()) continue;

                    // 2. CONVERSIONE A FLOAT E PULIZIA NAN (Fix Crash OpenCV)
                    // PatchNaNs richiede CV_32F. Se i dati sono Double, dobbiamo convertirli.
                    // Inoltre CV_32F è più leggero e sufficiente per l'export video.
                    Mat scientificMat = new Mat();
                    try
                    {
                        if (tempSourceMat.Depth() == MatType.CV_64F)
                        {
                            tempSourceMat.ConvertTo(scientificMat, MatType.CV_32F);
                        }
                        else
                        {
                            tempSourceMat.CopyTo(scientificMat);
                        }

                        // Ora siamo sicuri che è CV_32F -> Rimuoviamo i NaN (bordi neri dell'allineamento)
                        Cv2.PatchNaNs(scientificMat, 0.0);

                        // Orientamento corretto
                        Cv2.Flip(scientificMat, scientificMat, FlipMode.X);

                        double finalBlack, finalWhite;

                        // 3. CALCOLO SOGLIE
                        if (profile.IsAbsolute)
                        {
                            finalBlack = profile.Black;
                            finalWhite = profile.White;
                        }
                        else
                        {
                            // Ignora i pixel che sono esattamente 0.0 (i bordi neri creati da PatchNaNs)
                            // La maschera avrà valore 255 dove il pixel è valido, 0 dove è bordo
                            Cv2.Compare(scientificMat, 0.0, mask, CmpType.NE);
                            
                            // Se l'immagine è vuota o tutta nera
                            if (Cv2.CountNonZero(mask) == 0)
                            {
                                finalBlack = 0; finalWhite = 1;
                            }
                            else
                            {
                                // Calcoliamo statistiche solo sui pixel validi
                                Cv2.MeanStdDev(scientificMat, out var meanScalar, out var stdDevScalar, mask);
                                
                                double mean = meanScalar.Val0;
                                double sigma = stdDevScalar.Val0;

                                // Protezione matematica
                                if (double.IsNaN(mean) || double.IsNaN(sigma) || sigma < 1e-9) 
                                {
                                    // Fallback: usa Min/Max reali dell'area valida
                                    Cv2.MinMaxLoc(scientificMat, out double minVal, out double maxVal, out _, out _, mask);
                                    finalBlack = minVal;
                                    finalWhite = maxVal > minVal ? maxVal : minVal + 1.0;
                                }
                                else
                                {
                                    finalBlack = mean + (profile.KBlack * sigma);
                                    finalWhite = mean + (profile.KWhite * sigma);
                                }
                            }
                        }

                        // 4. NORMALIZZAZIONE E SCRITTURA FRAME
                        ApplyNormalizationToMat(scientificMat, frame8Bit, finalBlack, finalWhite, mode);
                        writer.Write(frame8Bit);
                    }
                    finally
                    {
                        // Importante: Rilasciare la matrice float ad ogni ciclo per non saturare la RAM
                        scientificMat.Dispose();
                    }
                }
            }
            finally
            {
                writer?.Dispose();
                frame8Bit?.Dispose();
            }
        });
    }
    
    // --- 3. NORMALIZZAZIONE CENTRALIZZATA (Core Logic) ---

    /// <summary>
    /// Applica la logica di visualizzazione (Linear/Log/Sqrt) convertendo da Float/Double a 8-bit.
    /// Usato sia per il video export che per l'anteprima UI.
    /// </summary>
    private void ApplyNormalizationToMat(Mat src, Mat dst8Bit, double black, double white, VisualizationMode mode)
    {
        double range = white - black;
        // Evitiamo divisioni per zero o range negativi assurdi
        if (Math.Abs(range) < 1e-9) range = 1e-5;

        if (mode == VisualizationMode.Linear)
        {
            double alpha = 255.0 / range;
            double beta = -black * alpha;

            // Protezioni overflow matematico
            if (double.IsInfinity(alpha) || double.IsNaN(alpha)) alpha = 0;
            if (double.IsInfinity(beta) || double.IsNaN(beta)) beta = 0;

            src.ConvertTo(dst8Bit, MatType.CV_8UC1, alpha, beta);
        }
        else
        {
            // Logica Non-Lineare (Log / Sqrt)
            
            // 1. Normalizzazione in range [0.0 ... 1.0] su matrice Float32
            double scale = 1.0 / range;
            double offset = -black * scale;

            using Mat tempMat = new Mat();
            src.ConvertTo(tempMat, MatType.CV_32FC1, scale, offset);

            // 2. Clipping dei valori fuori scala
            Cv2.Threshold(tempMat, tempMat, 0, 0, ThresholdTypes.Tozero); // < 0 -> 0
            Cv2.Threshold(tempMat, tempMat, 1, 1, ThresholdTypes.Trunc);  // > 1 -> 1

            // 3. Applicazione Funzione
            if (mode == VisualizationMode.SquareRoot)
            {
                Cv2.Sqrt(tempMat, tempMat);
            }
            else if (mode == VisualizationMode.Logarithmic)
            {
                // Log(1 + v) / Log(2) -> Scaling percettivo standard
                Cv2.Add(tempMat, 1.0, tempMat); 
                Cv2.Log(tempMat, tempMat); 
                Cv2.Multiply(tempMat, 1.442695, tempMat); // Moltiplicatore per base 2
            }

            // 4. Conversione finale a 8-bit [0...255]
            tempMat.ConvertTo(dst8Bit, MatType.CV_8UC1, 255.0, 0);
        }
    }

    // --- 4. NORMALIZZAZIONE UI (Avalonia Wrapper) ---

    public void NormalizeData(
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

        // Crea wrapper Mat attorno al buffer della bitmap Avalonia
        using Mat dstMat = Mat.FromPixelData(height, width, MatType.CV_8UC1, destinationBuffer, stride);
        
        // Riutilizza la logica core
        ApplyNormalizationToMat(sourceMat, dstMat, blackPoint, whitePoint, mode);
    }
    
    // --- 5. UTILITY MAT & FILES ---

    private Mat RawArrayToMat(Array rawData, int width, int height)
    {
        if (rawData.Rank == 1)
        {
            if (rawData is short[] sData) { var m = new Mat(height, width, MatType.CV_16SC1); Marshal.Copy(sData, 0, m.Data, sData.Length); return m; }
            if (rawData is float[] fData) { var m = new Mat(height, width, MatType.CV_32FC1); Marshal.Copy(fData, 0, m.Data, fData.Length); return m; }
            if (rawData is double[] dData) { var m = new Mat(height, width, MatType.CV_64FC1); Marshal.Copy(dData, 0, m.Data, dData.Length); return m; }
            if (rawData is int[] iData) { var m = new Mat(height, width, MatType.CV_32SC1); Marshal.Copy(iData, 0, m.Data, iData.Length); return m; }
            if (rawData is byte[] bData) { var m = new Mat(height, width, MatType.CV_8UC1); Marshal.Copy(bData, 0, m.Data, bData.Length); return m; }
        }
        
        if (rawData is IEnumerable enumerable)
        {
            Mat? mat = null;
            int row = 0;
            foreach (var item in enumerable)
            {
                if (item is Array rowArray)
                {
                    if (mat == null)
                    {
                        MatType type = MatType.CV_64FC1; 
                        if (rowArray is short[]) type = MatType.CV_16SC1;
                        else if (rowArray is float[]) type = MatType.CV_32FC1;
                        else if (rowArray is double[]) type = MatType.CV_64FC1;
                        else if (rowArray is int[]) type = MatType.CV_32SC1;
                        else if (rowArray is byte[]) type = MatType.CV_8UC1;
                        mat = new Mat(height, width, type);
                    }
                    if (row < height)
                    {
                        IntPtr rowPtr = mat.Ptr(row);
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

    private Stream OpenStream(string path)
    {
        if (path.StartsWith("avares://"))
        {
            var uri = new Uri(path);
            if (!AssetLoader.Exists(uri)) throw new FileNotFoundException($"Asset non trovato: {path}");
            return AssetLoader.Open(uri);
        }
        
        if (!File.Exists(path)) throw new FileNotFoundException($"File non trovato: {path}");

        // Retry Policy con FileShare.ReadWrite
        int maxRetries = 3;
        int delayMs = 100;

        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            }
            catch (IOException)
            {
                if (i == maxRetries - 1) throw;
                System.Threading.Thread.Sleep(delayMs);
            }
        }
        
        throw new IOException($"Impossibile aprire {path}");
    }
    
    // --- 6. METADATA E SALVATAGGIO ---

    public async Task<(int Width, int Height)> GetFitsImageSizeAsync(string path)
    {
        try 
        {
            using Stream streamToRead = OpenStream(path);
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
        catch
        {
            return (0, 0);
        }
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
            
                if (data.FitsHeader != null)
                {
                    var cursor = data.FitsHeader.GetCursor();
                    while (cursor.MoveNext())
                    {
                        HeaderCard? card = null;
                        if (cursor.Current is DictionaryEntry entry && entry.Value is HeaderCard hc) card = hc;
                        else if (cursor.Current is HeaderCard c) card = c;

                        if (card == null) continue;
                        
                        string key = card.Key?.Trim().ToUpper() ?? ""; 

                        if (key == "SIMPLE" || key == "BITPIX" || key == "EXTEND" || key.StartsWith("NAXIS") || key == "PCOUNT" || key == "GCOUNT") continue; 
                        
                        try
                        {
                            newHeader.AddCard(card);
                        }
                        catch { /* Ignora duplicati */ }
                    }
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

    public async Task<List<string>> SortFilesByObservationTimeAsync(List<string> filePaths)
    {
        if (filePaths.Count <= 1) return filePaths;

        return await Task.Run(() =>
        {
            var fileInfos = new System.Collections.Concurrent.ConcurrentBag<(string Path, DateTime? Date)>();
            Parallel.ForEach(filePaths, (path) =>
            {
                var date = TryGetObservationDate(path);
                fileInfos.Add((path, date));
            });
            return fileInfos.OrderBy(x => x.Date.HasValue ? 0 : 1).ThenBy(x => x.Date).ThenBy(x => x.Path).Select(x => x.Path).ToList();
        });
    }

    private DateTime? TryGetObservationDate(string path)
    {
        try
        {
            using Stream stream = OpenStream(path);
            var fitsFile = new Fits(stream);
            var hdu = fitsFile.ReadHDU();
            if (hdu == null) return null;
            Header header = hdu.Header;
            string? dateStr = header.GetStringValue("DATE-OBS");
            if (string.IsNullOrEmpty(dateStr)) dateStr = header.GetStringValue("DATE");
            if (!string.IsNullOrEmpty(dateStr) && DateTime.TryParse(dateStr, out DateTime dt)) return dt;
            return null;
        }
        catch { return null; }
    }

    public async Task<Header?> ReadHeaderOnlyAsync(string path)
    {
        return await Task.Run(() =>
        {
            try
            {
                using var stream = OpenStream(path);
                var fits = new nom.tam.fits.Fits(stream);
                var hdu = fits.ReadHDU();
                return hdu?.Header;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[FitsService] Errore lettura header {path}: {ex.Message}");
                return null;
            }
        });
    }
}