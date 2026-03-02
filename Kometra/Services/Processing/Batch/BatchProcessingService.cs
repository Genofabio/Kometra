using System;
using System.Collections.Generic;
using System.Diagnostics; // Necessario per Debug.WriteLine
using System.IO;
using System.Linq;
using System.Runtime; 
using System.Threading;
using System.Threading.Tasks;
using Kometra.Models.Fits;
using Kometra.Models.Fits.Structure;
using Kometra.Models.Processing;
using Kometra.Models.Processing.Batch;
using Kometra.Services.Fits;
using Kometra.Services.Fits.Conversion;
using Kometra.Services.Fits.Metadata;
using OpenCvSharp;

namespace Kometra.Services.Processing.Batch;

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
        
        Debug.WriteLine($"[Batch] AVVIO Batch su {files.Count} files. Cartella output: {outputFolder}");

        if (!Directory.Exists(outputFolder)) Directory.CreateDirectory(outputFolder);

        for (int i = 0; i < files.Count; i++)
        {
            // Controllo token PRIMA di iniziare il file
            if (token.IsCancellationRequested)
            {
                Debug.WriteLine("[Batch] Cancellazione richiesta prima dell'iterazione.");
                token.ThrowIfCancellationRequested();
            }

            var fileRef = files[i];
            string fileName = Path.GetFileName(fileRef.FilePath);
            
            Debug.WriteLine($"[Batch] ({i+1}/{files.Count}) Inizio elaborazione: {fileName}");
            progress?.Report(new BatchProgressReport(i + 1, files.Count, fileName, (double)(i + 1) / files.Count * 100));

            try
            {
                // 1. RECUPERO DATI
                Debug.WriteLine($"[Batch] ({i+1}) Caricamento dati da disco...");
                var data = await _dataManager.GetDataAsync(fileRef.FilePath);

                var imageHdu = data.FirstImageHdu ?? data.PrimaryHdu;
                if (imageHdu == null)
                {
                    Debug.WriteLine($"[Batch] SKIP ({i+1}): Nessuna HDU immagine valida.");
                    continue;
                }

                // 2. RECUPERO HEADER
                var sourceHeader = fileRef.ModifiedHeader ?? imageHdu.Header;
                FitsBitDepth originalDepth = _metadata.GetBitDepth(sourceHeader);

                // 3. ISOLAMENTO HEADER
                var workingHeader = _metadata.CloneHeader(sourceHeader);

                // 4. CONVERSIONE PIXEL
                Debug.WriteLine($"[Batch] ({i+1}) Conversione Raw -> Mat...");
                double bScale = _metadata.GetDoubleValue(sourceHeader, "BSCALE", 1.0);
                double bZero = _metadata.GetDoubleValue(sourceHeader, "BZERO", 0.0);
                
                using Mat srcMat = _converter.RawToMat(imageHdu.PixelData, bScale, bZero, FitsBitDepth.Double);
                using Mat dstMat = new Mat(srcMat.Size(), srcMat.Type());

                // 5. ESECUZIONE LOGICA
                Debug.WriteLine($"[Batch] ({i+1}) Esecuzione logica custom (Coordinator)...");
                await processorAsync(srcMat, dstMat, workingHeader, i);
                Debug.WriteLine($"[Batch] ({i+1}) Logica custom terminata.");

                // 6. SMART PROMOTION
                bool hasSpecialValues = !Cv2.CheckRange(dstMat, quiet: true);
                FitsBitDepth outputDepth = _metadata.ResolveOutputBitDepth(originalDepth, hasSpecialValues);

                // 7. FINALIZZAZIONE
                Debug.WriteLine($"[Batch] ({i+1}) Conversione Mat -> Raw ({outputDepth})...");
                var finalPixels = _converter.MatToRaw(dstMat, outputDepth);
                var finalHeader = _metadata.CreateHeaderFromTemplate(workingHeader, finalPixels, outputDepth);

                // 8. SALVATAGGIO
                Debug.WriteLine($"[Batch] ({i+1}) Salvataggio su disco...");
                var resultRef = await _dataManager.SaveAsTemporaryAsync(finalPixels, finalHeader, "BatchResult");
                
                results.Add(resultRef.FilePath);
                Debug.WriteLine($"[Batch] ({i+1}) Salvato in: {resultRef.FilePath}");

                // ---------------------------------------------------------------------------------
                // 9. PULIZIA AGGRESSIVA MEMORIA
                // ---------------------------------------------------------------------------------
                Debug.WriteLine($"[Batch] ({i+1}) Esecuzione GC cleanup...");
                
                finalPixels = null;
                imageHdu = null;
                data = null;

                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Debug.WriteLine($"[Batch] ({i+1}) Cleanup completato.");
                // ---------------------------------------------------------------------------------
            }
            catch (Exception ex)
            {
                // Stampiamo l'eccezione completa per capire se è Dispose o altro
                Debug.WriteLine($"[Batch] ERRORE FATALE sul file {fileName}: {ex}");
                Debug.WriteLine(ex.StackTrace);
            }
        }

        Debug.WriteLine($"[Batch] Ciclo finito. Restituzione {results.Count} risultati.");
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