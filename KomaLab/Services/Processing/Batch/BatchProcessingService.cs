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
    // Mantiene la compatibilità con le parti del progetto che usano semplici Action sincrone
    // =========================================================================================
    public Task<List<string>> ProcessFilesAsync(
        IEnumerable<FitsFileReference> sourceFiles,
        string outputFolder,
        Action<Mat, Mat, FitsHeader, int> processor,
        IProgress<BatchProgressReport>? progress = null,
        CancellationToken token = default)
    {
        // Avvolgiamo l'Action sincrona in un Func<Task> per riutilizzare il Core asincrono
        return ProcessFilesCoreAsync(sourceFiles, outputFolder, (s, d, h, i) => 
        {
            processor(s, d, h, i);
            return Task.CompletedTask;
        }, progress, token);
    }

    // =========================================================================================
    // OVERLOAD 2: Asincrono (Func<Task>) - [NUOVO]
    // Risolve il bug nel StructureExtractionCoordinator permettendo l'await corretto
    // =========================================================================================
    public Task<List<string>> ProcessFilesAsync(
        IEnumerable<FitsFileReference> sourceFiles,
        string outputFolder,
        Func<Mat, Mat, FitsHeader, int, Task> processor,
        IProgress<BatchProgressReport>? progress = null,
        CancellationToken token = default)
    {
        // Passiamo direttamente la funzione asincrona al Core
        return ProcessFilesCoreAsync(sourceFiles, outputFolder, processor, progress, token);
    }

    // =========================================================================================
    // CORE LOGIC (Private)
    // Contiene la logica unificata ed esegue l'await reale
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
                // 1. RECUPERO DATI E HEADER SORGENTE
                var data = await _dataManager.GetDataAsync(fileRef.FilePath);
                var sourceHeader = fileRef.ModifiedHeader ?? data.Header;
                FitsBitDepth originalDepth = _metadata.GetBitDepth(sourceHeader);

                // 2. ISOLAMENTO: Creiamo la copia di lavoro (workingHeader)
                var workingHeader = _metadata.CloneHeader(sourceHeader);

                // 3. CONVERSIONE E PREPARAZIONE MATRICI
                double bScale = _metadata.GetDoubleValue(sourceHeader, "BSCALE", 1.0);
                double bZero = _metadata.GetDoubleValue(sourceHeader, "BZERO", 0.0);
                
                using Mat srcMat = _converter.RawToMat(data.PixelData, bScale, bZero, FitsBitDepth.Double);
                using Mat dstMat = new Mat(srcMat.Size(), srcMat.Type());

                // 4. ESECUZIONE LOGICA (CRITICAL FIX)
                // Attendiamo il completamento del Task.
                // Il blocco 'using Mat srcMat' NON farà Dispose finché questa riga non è completata.
                await processorAsync(srcMat, dstMat, workingHeader, i);

                // 5. SMART PROMOTION (NaN Detection)
                bool hasSpecialValues = !Cv2.CheckRange(dstMat, quiet: true);
                FitsBitDepth outputDepth = _metadata.ResolveOutputBitDepth(originalDepth, hasSpecialValues);

                // 6. RICONVERSIONE E FINALIZZAZIONE HEADER
                var finalPixels = _converter.MatToRaw(dstMat, outputDepth);
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

    // Metodo helper (mantenuto per riferimento/uso interno se necessario)
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