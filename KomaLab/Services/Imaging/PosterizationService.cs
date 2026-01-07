using System;
using System.Collections; // Necessario per DictionaryEntry
using System.IO;
using System.Threading.Tasks;
using KomaLab.Models;
using KomaLab.Services.Data;
using nom.tam.fits;
using OpenCvSharp;

namespace KomaLab.Services.Imaging;

public class PosterizationService : IPosterizationService
{
    private readonly IFitsService _fitsService;
    private readonly IFitsDataConverter _converter;

    public PosterizationService(IFitsService fitsService, IFitsDataConverter converter)
    {
        _fitsService = fitsService;
        _converter = converter;
    }

    public async Task<string> PosterizeAndSaveAsync(
        string inputPath,
        string outputFolder,
        int levels,
        VisualizationMode mode,
        double blackPoint,
        double whitePoint)
    {
        return await Task.Run(async () =>
        {
            // 1. Caricamento
            var fitsData = await _fitsService.LoadFitsFromFileAsync(inputPath);
            if (fitsData == null) throw new FileNotFoundException($"File non trovato: {inputPath}");

            // 2. Conversione in Matrice OpenCV
            using Mat srcMat = _converter.RawToMat(fitsData);
            using Mat resultMat = new Mat();

            // 3. Calcolo Posterizzazione
            ComputePosterization(srcMat, resultMat, levels, mode, blackPoint, whitePoint);

            // 4. Conversione Output: Mat -> Jagged Array 2D (byte[][])
            // Questo formato dice implicitamente a CSharpFITS di impostare BITPIX=8 e NAXIS=2
            int w = resultMat.Width;
            int h = resultMat.Height;
            byte[][] jaggedPixels = new byte[h][];

            for (int y = 0; y < h; y++)
            {
                jaggedPixels[y] = new byte[w];
                var srcPtr = resultMat.Ptr(y);
                System.Runtime.InteropServices.Marshal.Copy(srcPtr, jaggedPixels[y], 0, w);
            }

            // 5. CLONAZIONE HEADER ROBUSTA (Stessa logica del ViewModel)
            var newHeader = CloneHeaderSafe(fitsData.FitsHeader);
            
            // Aggiungiamo la traccia della modifica in cima alla history
            newHeader.AddCard(new HeaderCard("HISTORY", $"KomaLab Posterization: {levels} levels, Mode: {mode}", null));

            // 6. Salvataggio
            string fileName = Path.GetFileNameWithoutExtension(inputPath);
            string outputPath = Path.Combine(outputFolder, $"{fileName}_Posterized.fits");

            var resultData = new FitsImageData
            {
                RawData = jaggedPixels,
                FitsHeader = newHeader,
                Width = w,
                Height = h
            };

            await _fitsService.SaveFitsFileAsync(resultData, outputPath);

            return outputPath;
        });
    }

    public static void ComputePosterization(Mat src, Mat dst, int levels, VisualizationMode mode, double bp, double wp)
    {
        double range = wp - bp;
        if (Math.Abs(range) < 1e-9) range = 1e-5;
        
        using Mat temp = new Mat();
        double scale = 1.0 / range;
        double offset = -bp * scale;

        src.ConvertTo(temp, MatType.CV_32FC1, scale, offset);
        Cv2.Max(temp, 0.0, temp);
        Cv2.Min(temp, 1.0, temp);

        if (mode == VisualizationMode.SquareRoot) Cv2.Sqrt(temp, temp);
        else if (mode == VisualizationMode.Logarithmic)
        {
            Cv2.Add(temp, 1.0, temp);
            Cv2.Log(temp, temp);
            Cv2.Multiply(temp, 1.442695, temp);
        }

        Cv2.Multiply(temp, (double)levels - 0.001, temp);
        using Mat intTemp = new Mat();
        temp.ConvertTo(intTemp, MatType.CV_32SC1); 
        intTemp.ConvertTo(temp, MatType.CV_32FC1);
        
        double divScale = levels > 1 ? 1.0 / (levels - 1) : 1.0;
        Cv2.Multiply(temp, divScale, temp);

        temp.ConvertTo(dst, MatType.CV_8UC1, 255.0, 0);
    }

    /// <summary>
    /// Clona l'header usando la stessa logica di estrazione robusta del HeaderEditorViewModel.
    /// Recupera correttamente COMMENT, HISTORY e chiavi duplicate gestendo DictionaryEntry.
    /// </summary>
    private Header CloneHeaderSafe(Header original)
    {
        var newHeader = new Header();
        var cursor = original.GetCursor();

        while (cursor.MoveNext())
        {
            // --- LOGICA ESTRAZIONE (Identica al tuo ViewModel) ---
            HeaderCard? card = null;

            // Caso 1: Oggetto diretto
            if (cursor.Current is HeaderCard hc) 
            {
                card = hc;
            }
            // Caso 2: DictionaryEntry (fondamentale per COMMENT/HISTORY/Duplicate)
            else if (cursor.Current is DictionaryEntry de && de.Value is HeaderCard hcd) 
            {
                card = hcd;
            }

            // Se null, non possiamo farci nulla
            if (card == null) continue;
            // -----------------------------------------------------

            string key = card.Key?.Trim().ToUpper() ?? "";

            // --- FILTRO BLACKLIST (Necessario per validità 8-bit) ---
            
            // 1. Strutturali (rigenerate automaticamente)
            if (key == "SIMPLE" || 
                key == "BITPIX" || 
                key == "NAXIS" || 
                key == "NAXIS1" || 
                key == "NAXIS2" || 
                key == "EXTEND" || 
                key == "PCOUNT" || 
                key == "GCOUNT")
            {
                continue;
            }

            // 2. Scaling (DEVONO essere rimosse, altrimenti l'immagine 8-bit diventa bianca)
            if (key == "BSCALE" || key == "BZERO")
            {
                continue;
            }

            // 3. Checksum (non più validi)
            if (key == "CHECKSUM" || key == "DATASUM")
            {
                continue;
            }

            // --- COPIA DIRETTA ---
            // Aggiungiamo la card così com'è. 
            // Questo preserva HIERARCH, COMMENT e HISTORY esattamente come sono stati letti.
            newHeader.AddCard(card);
        }

        return newHeader;
    }
}