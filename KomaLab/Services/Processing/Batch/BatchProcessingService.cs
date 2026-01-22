using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KomaLab.Models.Fits;
using KomaLab.Models.Fits.Structure;
using KomaLab.Models.Processing;
using KomaLab.Services.Fits;
using KomaLab.Services.Fits.Conversion;
using KomaLab.Services.Fits.Metadata;
using OpenCvSharp;

namespace KomaLab.Services.Processing.Batch;

public class BatchProcessingService : IBatchProcessingService
{
    private readonly IFitsDataManager _dataManager;
    private readonly IFitsMetadataService _metadata;
    private readonly IFitsOpenCvConverter _converter;

    public BatchProcessingService(
        IFitsDataManager dataManager,
        IFitsMetadataService metadata,
        IFitsOpenCvConverter converter)
    {
        _dataManager = dataManager ?? throw new ArgumentNullException(nameof(dataManager));
        _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        _converter = converter ?? throw new ArgumentNullException(nameof(converter));
    }

    public async Task<List<string>> ProcessFilesAsync(
        IEnumerable<FitsFileReference> sourceFiles,
        string outputFolder,
        Action<Mat, Mat, FitsHeader, int> processor, // Firma aggiornata
        IProgress<BatchProgressReport>? progress = null,
        CancellationToken token = default)
    {
        var files = sourceFiles.ToList();
        var results = new List<string>();
        
        if (!Directory.Exists(outputFolder)) Directory.CreateDirectory(outputFolder);

        for (int i = 0; i < files.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var fileRef = files[i];
            
            progress?.Report(new BatchProgressReport(i + 1, files.Count, Path.GetFileName(fileRef.FilePath), (double)(i + 1) / files.Count * 100));

            try
            {
                // 1. RECUPERO DATI E HEADER SORGENTE
                var data = await _dataManager.GetDataAsync(fileRef.FilePath);
                var sourceHeader = fileRef.ModifiedHeader ?? data.Header;
                FitsBitDepth originalDepth = _metadata.GetBitDepth(sourceHeader);

                // 2. ISOLAMENTO: Creiamo la copia di lavoro (workingHeader)
                // Questo evita di "sporcare" i metadati del file originale in RAM
                var workingHeader = _metadata.CloneHeader(sourceHeader);

                // 3. CONVERSIONE E PREPARAZIONE MATRICI
                double bScale = _metadata.GetDoubleValue(sourceHeader, "BSCALE", 1.0);
                double bZero = _metadata.GetDoubleValue(sourceHeader, "BZERO", 0.0);
                
                using Mat srcMat = _converter.RawToMat(data.PixelData, bScale, bZero, FitsBitDepth.Double);
                using Mat dstMat = new Mat(srcMat.Size(), srcMat.Type());

                // 4. ESECUZIONE LOGICA: Il processor ora ha tutto per lavorare
                // Modificherà dstMat (pixel) e workingHeader (WCS, History)
                processor(srcMat, dstMat, workingHeader, i);

                // 5. SMART PROMOTION (NaN Detection)
                bool hasSpecialValues = !Cv2.CheckRange(dstMat, quiet: true);
                FitsBitDepth outputDepth = _metadata.ResolveOutputBitDepth(originalDepth, hasSpecialValues);

                // 6. RICONVERSIONE E FINALIZZAZIONE HEADER
                var finalPixels = _converter.MatToRaw(dstMat, outputDepth);
                
                // CreateHeaderFromTemplate prenderà il workingHeader (già shiftato nel WCS)
                // e scriverà NAXIS e BITPIX definitivi in cima al nuovo header.
                var finalHeader = _metadata.CreateHeaderFromTemplate(workingHeader, finalPixels, outputDepth);

                // 7. SALVATAGGIO
                var resultRef = await _dataManager.SaveAsTemporaryAsync(finalPixels, finalHeader, "BatchResult");
                results.Add(resultRef.FilePath);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Batch Error [{fileRef.FilePath}]: {ex.Message}");
            }
        }
        return results;
    }

    /// <summary>
    /// Decide se mantenere il formato originale o promuoverlo a Floating Point.
    /// Segue le regole: Int32 -> Double (-64), Int16/Byte -> Float (-32) se ci sono NaN.
    /// </summary>
    private FitsBitDepth DetermineOptimalOutputDepth(FitsBitDepth original, Mat processedMat)
    {
        // Se il file è già in virgola mobile, non facciamo downgrade
        if ((int)original < 0) return original;

        // Verifichiamo la presenza di NaN o Infiniti (tipici del padding post-allineamento)
        bool hasSpecialValues = !Cv2.CheckRange(processedMat, quiet: true);

        if (hasSpecialValues)
        {
            // Promozione intelligente per preservare la precisione dei dati scientifici
            // - Gli interi a 32 bit necessitano di Double per non perdere precisione nella mantissa
            // - Per 8 e 16 bit, il Float (32 bit) è più che sufficiente
            return (original == FitsBitDepth.Int32) 
                ? FitsBitDepth.Double 
                : FitsBitDepth.Float;
        }

        return original;
    }
}