using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KomaLab.Models.Fits;
using KomaLab.Models.Fits.Structure;
using KomaLab.Models.Processing;
using KomaLab.Services.Fits;
using KomaLab.Services.Fits.Conversion;
using KomaLab.Services.Fits.Metadata;
using KomaLab.Services.Processing.Engines;
using OpenCvSharp;

namespace KomaLab.Services.Processing.Coordinators;

public class StackingCoordinator : IStackingCoordinator
{
    private readonly IFitsDataManager _dataManager;
    private readonly IFitsMetadataService _metadataService;
    private readonly IFitsOpenCvConverter _converter;
    private readonly IStackingEngine _stackingEngine;

    public StackingCoordinator(
        IFitsDataManager dataManager,
        IFitsMetadataService metadataService,
        IFitsOpenCvConverter converter,
        IStackingEngine stackingEngine)
    {
        _dataManager = dataManager ?? throw new ArgumentNullException(nameof(dataManager));
        _metadataService = metadataService ?? throw new ArgumentNullException(nameof(metadataService));
        _converter = converter ?? throw new ArgumentNullException(nameof(converter));
        _stackingEngine = stackingEngine ?? throw new ArgumentNullException(nameof(stackingEngine));
    }

    public async Task<FitsFileReference> ExecuteStackingAsync(IEnumerable<FitsFileReference> sourceFiles, StackingMode mode)
    {
        var fileList = sourceFiles.ToList();
        if (fileList.Count < 2) 
            throw new InvalidOperationException("Sono necessarie almeno 2 immagini per eseguire lo stacking.");

        // 1. Recuperiamo dimensioni e header base dal primo frame
        // Questo serve sia per inizializzare gli accumulatori sia come template per l'header finale
        var firstFrameData = await _dataManager.GetDataAsync(fileList[0].FilePath);
        var baseHeader = fileList[0].ModifiedHeader ?? firstFrameData.Header;
        int width = _metadataService.GetIntValue(baseHeader, "NAXIS1");
        int height = _metadataService.GetIntValue(baseHeader, "NAXIS2");

        Mat finalMat = null;

        // =================================================================================
        // STRATEGIA A: STACKING INCREMENTALE (Low Memory)
        // Usata per Somma e Media: carica 1 file, accumula, scarica subito.
        // =================================================================================
        if (mode == StackingMode.Sum || mode == StackingMode.Average)
        {
            // Nota: Assicurati che StackingEngine implementi questi metodi pubblici (o usa un cast se sono sull'implementazione concreta)
            // Se IStackingEngine non li espone ancora, dovrai aggiornare l'interfaccia.
            // Qui assumo un cast sicuro all'implementazione concreta per accedere alla logica incrementale.
            if (_stackingEngine is StackingEngine engine)
            {
                engine.InitializeAccumulators(width, height, out Mat accumulator, out Mat countMap);

                try
                {
                    foreach (var fileRef in fileList)
                    {
                        var data = await _dataManager.GetDataAsync(fileRef.FilePath);
                        var h = fileRef.ModifiedHeader ?? data.Header;
                        double bScale = _metadataService.GetDoubleValue(h, "BSCALE", 1.0);
                        double bZero = _metadataService.GetDoubleValue(h, "BZERO", 0.0);

                        // Carichiamo e convertiamo al volo (Double per precisione)
                        using var currentMat = _converter.RawToMat(data.PixelData, bScale, bZero, FitsBitDepth.Double);
                        
                        // Accumuliamo nel buffer statico
                        engine.AccumulateFrame(accumulator, countMap, currentMat);
                        
                        // 'currentMat' viene disposto qui, liberando ~200MB di RAM immediatamente
                    }

                    if (mode == StackingMode.Average)
                    {
                        engine.FinalizeAverage(accumulator, countMap);
                    }
                    
                    finalMat = accumulator; // Trasferiamo la proprietà del risultato
                }
                finally
                {
                    countMap.Dispose();
                    // Non disponiamo 'accumulator' qui se è diventato 'finalMat', 
                    // altrimenti disponiamo anche quello in caso di errore.
                    if (finalMat == null) accumulator.Dispose();
                }
            }
            else
            {
                // Fallback se l'engine non è quello atteso (non dovrebbe accadere con DI corretta)
                throw new InvalidOperationException("L'engine di stacking configurato non supporta la modalità incrementale.");
            }
        }
        // =================================================================================
        // STRATEGIA B: STACKING MASSIVO (High Memory)
        // Usata per Mediana: richiede tutti i pixel per calcolare la statistica.
        // =================================================================================
        else 
        {
            var matsToDispose = new List<Mat>();
            try
            {
                foreach (var fileRef in fileList)
                {
                    var data = await _dataManager.GetDataAsync(fileRef.FilePath);
                    var h = fileRef.ModifiedHeader ?? data.Header;
                    double bScale = _metadataService.GetDoubleValue(h, "BSCALE", 1.0);
                    double bZero = _metadataService.GetDoubleValue(h, "BZERO", 0.0);
                    
                    // Qui purtroppo dobbiamo tenerle tutte in vita
                    matsToDispose.Add(_converter.RawToMat(data.PixelData, bScale, bZero, FitsBitDepth.Double));
                }
                
                finalMat = await _stackingEngine.ComputeStackAsync(matsToDispose, mode);
            }
            finally
            {
                foreach (var m in matsToDispose) m.Dispose();
            }
        }

        // =================================================================================
        // 3. FINALIZZAZIONE E SALVATAGGIO
        // =================================================================================
        try
        {
            using (finalMat) // Assicura che la matrice risultato venga pulita dopo la conversione
            {
                // Riconversione in array grezzo (Manteniamo Double per dati scientifici)
                var finalPixels = _converter.MatToRaw(finalMat, FitsBitDepth.Double);

                var finalHeader = _metadataService.CreateHeaderFromTemplate(
                    baseHeader, 
                    finalPixels, 
                    FitsBitDepth.Double
                );

                // --- Metadati ---
                _metadataService.SetValue(finalHeader, "NCOMBINE", fileList.Count, "KomaLab - Number of combined frames");
                _metadataService.SetValue(finalHeader, "STACKMET", mode.ToString().ToUpper(), "KomaLab - Stacking algorithm used");
                _metadataService.SetValue(finalHeader, "DATE-PRO", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss"), "KomaLab - Processing timestamp (UTC)");
                _metadataService.AddValue(finalHeader, "HISTORY", $"KomaLab - Stacking: Combined {fileList.Count} frames via {mode} algorithm.", null);

                // Salvataggio
                return await _dataManager.SaveAsTemporaryAsync(finalPixels, finalHeader, "StackResult");
            }
        }
        catch
        {
            // Sicurezza extra: se qualcosa fallisce nel salvataggio, puliamo la matrice
            if (finalMat != null && !finalMat.IsDisposed) finalMat.Dispose();
            throw;
        }
    }
}