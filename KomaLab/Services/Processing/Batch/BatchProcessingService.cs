using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KomaLab.Models.Fits;
using KomaLab.Models.Processing;
using KomaLab.Services.Fits;
using KomaLab.Services.Fits.Conversion;
using KomaLab.Services.Fits.Metadata;
using OpenCvSharp;

namespace KomaLab.Services.Processing.Batch;

/// <summary>
/// Esecutore universale per operazioni batch (Capocantiere).
/// Gestisce il ciclo di vita del processing massivo su file FITS.
/// </summary>
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
        IEnumerable<string> sourcePaths,
        string outputFolder,
        Action<Mat, Mat, int> processor,
        IProgress<BatchProgressReport>? progress = null,
        CancellationToken token = default)
    {
        var paths = sourcePaths.ToList();
        var results = new List<string>();
        
        if (!Directory.Exists(outputFolder)) 
            Directory.CreateDirectory(outputFolder);

        for (int i = 0; i < paths.Count; i++)
        {
            // 1. Controllo Cancellazione
            token.ThrowIfCancellationRequested();

            string currentPath = paths[i];
            
            // Notifica Progresso alla UI
            progress?.Report(new BatchProgressReport(
                i + 1, 
                paths.Count, 
                Path.GetFileName(currentPath), 
                (double)(i + 1) / paths.Count * 100));

            try
            {
                // 2. Recupero Dati (DataManager gestisce la Cache)
                var data = await _dataManager.GetDataAsync(currentPath);
                
                // 3. Estrazione Metadati di Scalatura e Profondità (MetadataService)
                // Usiamo i fallback per garantire che il processo non si interrompa
                double bScale = _metadata.GetDoubleValue(data.Header, "BSCALE", 1.0);
                double bZero = _metadata.GetDoubleValue(data.Header, "BZERO", 0.0);
                
                // Determiniamo la profondità di bit originale (BITPIX) per l'output
                FitsBitDepth originalDepth = _metadata.GetBitDepth(data.Header);

                // 4. Conversione e Preparazione Matrici (Converter)
                using Mat srcMat = _converter.RawToMat(data.PixelData, bScale, bZero);
                using Mat dstMat = new Mat(srcMat.Size(), srcMat.Type());

                // 5. ESECUZIONE DELLA LOGICA (Delegata al chiamante)
                // Qui viene iniettata la funzione di Posterizzazione, Filtro, ecc.
                processor(srcMat, dstMat, i); // Passiamo 'i' (l'indice corrente)

                // 6. Riconversione in Array RAW (Mantenendo la profondità originale)
                var finalPixels = _converter.MatToRaw(dstMat, originalDepth);

                // 7. Generazione Header FITS
                // CreateHeaderFromTemplate si occuperà di aggiornare BITPIX, NAXIS, ecc.
                var finalHeader = _metadata.CreateHeaderFromTemplate(data.Header, finalPixels, originalDepth);
                
                _metadata.AddValue(finalHeader, "HISTORY", $"KomaLab Processed: {DateTime.UtcNow:yyyy-MM-dd HH:mm}");

                // 8. Salvataggio Fisico (DataManager gestisce la destinazione e la registrazione)
                var fileRef = await _dataManager.SaveAsTemporaryAsync(finalPixels, finalHeader, "BatchResult");
                results.Add(fileRef.FilePath);
            }
            catch (Exception ex)
            {
                // Log dell'errore sul singolo file per non interrompere l'intero batch
                System.Diagnostics.Debug.WriteLine($"Batch Error [{currentPath}]: {ex.Message}");
                // Opzionale: aggiungere alla lista dei risultati un segnale di errore
            }
        }

        return results;
    }
}