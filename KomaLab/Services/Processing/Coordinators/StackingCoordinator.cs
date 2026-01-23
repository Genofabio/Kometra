using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Channels; // Necessario per il Producer-Consumer
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

        // 1. Setup Iniziale (Dimensioni e Header base)
        var firstFrameData = await _dataManager.GetDataAsync(fileList[0].FilePath);
        var baseHeader = fileList[0].ModifiedHeader ?? firstFrameData.Header;
        int width = _metadataService.GetIntValue(baseHeader, "NAXIS1");
        int height = _metadataService.GetIntValue(baseHeader, "NAXIS2");

        Mat finalMat = null;

        // =================================================================================
        // STRATEGIA 1: MEDIANA (Chunked / Low Memory)
        // =================================================================================
        if (mode == StackingMode.Median)
        {
            // Passiamo all'engine la lista dei file e una funzione (Lambda) che spiega 
            // come caricare un singolo "pezzo" (ROI) di un'immagine quando richiesto.
            finalMat = await _stackingEngine.ComputeMedianChunkedAsync(
                fileList, 
                width, 
                height, 
                async (fileRef, rect) => 
                {
                    // A. Caricamento Dati (IO)
                    var data = await _dataManager.GetDataAsync(fileRef.FilePath);
                    var h = fileRef.ModifiedHeader ?? data.Header;
                    double bScale = _metadataService.GetDoubleValue(h, "BSCALE", 1.0);
                    double bZero = _metadataService.GetDoubleValue(h, "BZERO", 0.0);

                    // B. Conversione ROI
                    // Carichiamo l'intera immagine (transiente), ritagliamo il pezzo e disponiamo l'originale.
                    // Questo garantisce che occupiamo RAM solo per 1 immagine intera alla volta.
                    using var fullMat = _converter.RawToMat(data.PixelData, bScale, bZero, FitsBitDepth.Double);
                    
                    // C. Estrazione Sottomatrice
                    // .Clone() è fondamentale: copia i dati in una nuova memoria piccola, 
                    // slegandosi da 'fullMat' che verrà distrutta alla fine del blocco using.
                    return fullMat.SubMat(rect).Clone();
                });
        }
        // =================================================================================
        // STRATEGIA 2: SOMMA/MEDIA (Producer-Consumer Pipeline)
        // =================================================================================
        else 
        {
            // Usiamo il casting per accedere ai metodi incrementali se non sono nell'interfaccia IStackingEngine
            // (Consiglio: aggiungi InitializeAccumulators/AccumulateFrame all'interfaccia IStackingEngine)
            if (_stackingEngine is StackingEngine engine)
            {
                engine.InitializeAccumulators(width, height, out Mat accumulator, out Mat countMap);

                // Creiamo un canale limitato. Permette di pre-caricare fino a 3 immagini in memoria
                // mentre il consumatore le elabora. Se il canale è pieno, il produttore aspetta.
                var channel = Channel.CreateBounded<Mat>(new BoundedChannelOptions(3)
                {
                    SingleWriter = true,
                    SingleReader = true,
                    FullMode = BoundedChannelFullMode.Wait
                });

                // --- TASK PRODUTTORE (Caricamento IO & Conversione) ---
                var producerTask = Task.Run(async () =>
                {
                    try
                    {
                        foreach (var fileRef in fileList)
                        {
                            var data = await _dataManager.GetDataAsync(fileRef.FilePath);
                            var h = fileRef.ModifiedHeader ?? data.Header;
                            double bScale = _metadataService.GetDoubleValue(h, "BSCALE", 1.0);
                            double bZero = _metadataService.GetDoubleValue(h, "BZERO", 0.0);

                            // Convertiamo e inviamo al canale
                            var mat = _converter.RawToMat(data.PixelData, bScale, bZero, FitsBitDepth.Double);
                            await channel.Writer.WriteAsync(mat);
                        }
                    }
                    catch (Exception ex)
                    {
                        channel.Writer.Complete(ex); // Segnala errore al consumatore
                    }
                    finally
                    {
                        channel.Writer.Complete(); // Segnala fine dati
                    }
                });

                // --- CONSUMATORE (Accumulo su Thread Corrente) ---
                try
                {
                    // Leggiamo dal canale man mano che arrivano i dati
                    await foreach (var mat in channel.Reader.ReadAllAsync())
                    {
                        using (mat) // Importante: Dispone la Mat appena finiamo di accumularla
                        {
                            engine.AccumulateFrame(accumulator, countMap, mat);
                        }
                    }

                    // Attendiamo che il produttore finisca "pulito" (per rilanciare eventuali eccezioni)
                    await producerTask;

                    if (mode == StackingMode.Average)
                    {
                        engine.FinalizeAverage(accumulator, countMap);
                    }
                    
                    finalMat = accumulator; // Accumulator diventa il risultato finale
                }
                catch
                {
                    // Se qualcosa va storto nel loop, puliamo l'accumulatore se non è ancora finalMat
                    if (finalMat == null) accumulator.Dispose();
                    throw;
                }
                finally
                {
                    countMap.Dispose(); // La mappa conteggi non serve più
                }
            }
            else
            {
                throw new InvalidOperationException("L'engine configurato non supporta lo stacking incrementale.");
            }
        }

        // =================================================================================
        // 3. FINALIZZAZIONE E SALVATAGGIO (Comune)
        // =================================================================================
        try
        {
            using (finalMat) 
            {
                var finalPixels = _converter.MatToRaw(finalMat, FitsBitDepth.Double);
                var finalHeader = _metadataService.CreateHeaderFromTemplate(baseHeader, finalPixels, FitsBitDepth.Double);

                // Metadata
                _metadataService.SetValue(finalHeader, "NCOMBINE", fileList.Count, "Number of combined frames");
                _metadataService.SetValue(finalHeader, "STACKMET", mode.ToString().ToUpper(), "Stacking algorithm");
                _metadataService.SetValue(finalHeader, "DATE-PRO", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss"), "Processing timestamp");

                return await _dataManager.SaveAsTemporaryAsync(finalPixels, finalHeader, "StackResult");
            }
        }
        catch
        {
            if (finalMat != null && !finalMat.IsDisposed) finalMat.Dispose();
            throw;
        }
    }
}