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

    // =========================================================================================
    // OVERLOAD 1: Sincrono / Legacy (Action)
    // =========================================================================================
    public Task<List<string>> ProcessFilesAsync(
        IEnumerable<FitsFileReference> sourceFiles,
        string outputFolder,
        Action<Mat, Mat, FitsHeader, int> processor,
        IProgress<BatchProgressReport>? progress = null,
        CancellationToken token = default)
    {
        return ProcessFilesCoreAsync(sourceFiles, outputFolder, (s, d, h, i) => 
        {
            processor(s, d, h, i);
            return Task.CompletedTask;
        }, progress, token);
    }

    // =========================================================================================
    // OVERLOAD 2: Asincrono (Func<Task>)
    // =========================================================================================
    public Task<List<string>> ProcessFilesAsync(
        IEnumerable<FitsFileReference> sourceFiles,
        string outputFolder,
        Func<Mat, Mat, FitsHeader, int, Task> processor,
        IProgress<BatchProgressReport>? progress = null,
        CancellationToken token = default)
    {
        return ProcessFilesCoreAsync(sourceFiles, outputFolder, processor, progress, token);
    }

    // =========================================================================================
    // CORE LOGIC (Private)
    // =========================================================================================
    private async Task<List<string>> ProcessFilesCoreAsync(
        IEnumerable<FitsFileReference> sourceFiles,
        string outputFolder,
        Func<Mat, Mat, FitsHeader, int, Task> processorAsync,
        IProgress<BatchProgressReport>? progress,
        CancellationToken token)
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
                // 1. RECUPERO DATI
                var data = await _dataManager.GetDataAsync(fileRef.FilePath);

                // [MODIFICA MEF] Accesso sicuro all'HDU immagine
                // FitsDataPackage ora è una lista di HDU, non contiene più PixelData diretti.
                var imageHdu = data.FirstImageHdu ?? data.PrimaryHdu;

                if (imageHdu == null)
                {
                    System.Diagnostics.Debug.WriteLine($"Batch Skip [{fileRef.FilePath}]: No valid image HDU found.");
                    continue;
                }

                // 2. RECUPERO HEADER
                // Usiamo l'header modificato in RAM (se esiste), altrimenti quello dell'HDU
                var sourceHeader = fileRef.ModifiedHeader ?? imageHdu.Header;
                
                FitsBitDepth originalDepth = _metadata.GetBitDepth(sourceHeader);

                // 3. ISOLAMENTO: Creiamo la copia di lavoro dell'header
                var workingHeader = _metadata.CloneHeader(sourceHeader);

                // 4. CONVERSIONE PIXEL
                double bScale = _metadata.GetDoubleValue(sourceHeader, "BSCALE", 1.0);
                double bZero = _metadata.GetDoubleValue(sourceHeader, "BZERO", 0.0);
                
                // Usiamo imageHdu.PixelData invece di data.PixelData
                using Mat srcMat = _converter.RawToMat(imageHdu.PixelData, bScale, bZero, FitsBitDepth.Double);
                using Mat dstMat = new Mat(srcMat.Size(), srcMat.Type());

                // 5. ESECUZIONE LOGICA
                await processorAsync(srcMat, dstMat, workingHeader, i);

                // 6. SMART PROMOTION
                bool hasSpecialValues = !Cv2.CheckRange(dstMat, quiet: true);
                FitsBitDepth outputDepth = _metadata.ResolveOutputBitDepth(originalDepth, hasSpecialValues);

                // 7. FINALIZZAZIONE
                var finalPixels = _converter.MatToRaw(dstMat, outputDepth);
                var finalHeader = _metadata.CreateHeaderFromTemplate(workingHeader, finalPixels, outputDepth);

                // 8. SALVATAGGIO
                var resultRef = await _dataManager.SaveAsTemporaryAsync(finalPixels, finalHeader, "BatchResult");
                results.Add(resultRef.FilePath);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Batch Error [{fileRef.FilePath}]: {ex.Message}");
                // Opzionale: Rilanciare o gestire in base alle policy di errore
            }
        }
        return results;
    }

    private FitsBitDepth DetermineOptimalOutputDepth(FitsBitDepth original, Mat processedMat)
    {
        if ((int)original < 0) return original;
        bool hasSpecialValues = !Cv2.CheckRange(processedMat, quiet: true);

        if (hasSpecialValues)
        {
            return (original == FitsBitDepth.Int32) 
                ? FitsBitDepth.Double 
                : FitsBitDepth.Float;
        }
        return original;
    }
}