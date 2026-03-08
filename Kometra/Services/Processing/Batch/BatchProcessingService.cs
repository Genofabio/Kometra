using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime; 
using System.Threading;
using System.Threading.Tasks;
using Kometra.Models.Fits;
using Kometra.Models.Fits.Structure;
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

        if (!Directory.Exists(outputFolder)) Directory.CreateDirectory(outputFolder);

        for (int i = 0; i < files.Count; i++)
        {
            // Controllo token PRIMA di iniziare il file
            if (token.IsCancellationRequested)
            {
                token.ThrowIfCancellationRequested();
            }

            var fileRef = files[i];
            string fileName = Path.GetFileName(fileRef.FilePath);
            
            progress?.Report(new BatchProgressReport(i + 1, files.Count, fileName, (double)(i + 1) / files.Count * 100));

            try
            {
                // 1. RECUPERO DATI
                var data = await _dataManager.GetDataAsync(fileRef.FilePath);

                var imageHdu = data.FirstImageHdu ?? data.PrimaryHdu;
                if (imageHdu == null)
                {
                    continue;
                }

                // 2. RECUPERO HEADER
                var sourceHeader = fileRef.ModifiedHeader ?? imageHdu.Header;
                FitsBitDepth originalDepth = _metadata.GetBitDepth(sourceHeader);

                // 3. ISOLAMENTO HEADER
                var workingHeader = _metadata.CloneHeader(sourceHeader);

                // 4. CONVERSIONE PIXEL
                double bScale = _metadata.GetDoubleValue(sourceHeader, "BSCALE", 1.0);
                double bZero = _metadata.GetDoubleValue(sourceHeader, "BZERO", 0.0);
                
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

                // ---------------------------------------------------------------------------------
                // 9. PULIZIA AGGRESSIVA MEMORIA
                // ---------------------------------------------------------------------------------
                
                finalPixels = null;
                imageHdu = null;
                data = null;

                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect();
                GC.WaitForPendingFinalizers();
                // ---------------------------------------------------------------------------------
            }
            catch (Exception ex)
            {
            }
        }
        
        return results;
    }
}