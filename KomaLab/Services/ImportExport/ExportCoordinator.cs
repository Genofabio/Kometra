using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KomaLab.Models.Export;
using KomaLab.Models.Fits.Structure;
using KomaLab.Models.Processing;
using KomaLab.Models.Visualization;
using KomaLab.Services.Fits;
using KomaLab.Services.Fits.IO;
using KomaLab.Services.Fits.Metadata;

namespace KomaLab.Services.ImportExport;

public class ExportCoordinator : IExportCoordinator
{
    private readonly IFitsDataManager _dataManager;
    private readonly IFitsMetadataService _metadataService;
    private readonly IFitsIoService _ioService;
    private readonly IBitmapExportService _bitmapService;

    public ExportCoordinator(
        IFitsDataManager dataManager,
        IFitsMetadataService metadataService,
        IFitsIoService ioService,
        IBitmapExportService bitmapService)
    {
        _dataManager = dataManager ?? throw new ArgumentNullException(nameof(dataManager));
        _metadataService = metadataService ?? throw new ArgumentNullException(nameof(metadataService));
        _ioService = ioService ?? throw new ArgumentNullException(nameof(ioService));
        _bitmapService = bitmapService ?? throw new ArgumentNullException(nameof(bitmapService));
    }

    public async Task ExecuteExportAsync(
        IEnumerable<ExportableItem> items,
        ExportJobSettings settings,
        IProgress<BatchProgressReport> progress,
        CancellationToken token)
    {
        var queue = items.Where(i => i.IsSelected).ToList();
        if (queue.Count == 0) return;

        if (!Directory.Exists(settings.OutputDirectory))
            Directory.CreateDirectory(settings.OutputDirectory);

        // Se Merge è attivo, usiamo la logica MEF (Multi-Extension FITS)
        if (settings.Format == ExportFormat.Fits && settings.MergeIntoSingleFile && queue.Count > 1)
        {
            await ExportMergedFitsAsync(queue, settings, progress, token);
        }
        else
        {
            await ExportSingleFilesAsync(queue, settings, progress, token);
        }
    }

    private async Task ExportMergedFitsAsync(
        List<ExportableItem> items,
        ExportJobSettings settings,
        IProgress<BatchProgressReport> progress,
        CancellationToken token)
    {
        string fileName = !string.IsNullOrEmpty(settings.BaseFileName) ? settings.BaseFileName : "Merged_Sequence";
        if (!fileName.EndsWith(".fits", StringComparison.OrdinalIgnoreCase)) fileName += ".fits";
        string finalPath = Path.Combine(settings.OutputDirectory, fileName);

        var sourceData = new List<(Array Pixels, FitsHeader Header)>();

        // 1. Caricamento dati
        for (int i = 0; i < items.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var item = items[i];
            progress.Report(new BatchProgressReport(i + 1, items.Count, $"Analisi: {item.FileName}", (double)i / items.Count * 50));

            try
            {
                var package = await _dataManager.GetDataAsync(item.FullPath);
                var (pixels, header) = ExtractPrimaryData(package);
                
                // Nota: Non cloniamo qui, lo facciamo appena prima di assemblare i blocchi
                if (pixels != null && header != null) sourceData.Add((pixels, header));
            }
            catch (Exception ex)
            {
                item.Status = "Errore Lettura";
                item.IsError = true;
                Debug.WriteLine($"Merge Error: {ex.Message}");
            }
        }

        if (sourceData.Count == 0) return;

        // 2. Creazione dell'HDU Primario "Null" (contenitore vuoto)
        var commonHeader = CreateCommonHeader(sourceData.Select(d => d.Header).ToList());
        _metadataService.AddValue(commonHeader, "HISTORY", $"KomaLab MEF Container created: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        _metadataService.AddValue(commonHeader, "CREATOR", "KomaLab Processing Suite");

        var blocks = new List<(Array Pixels, FitsHeader Header)>();
        
        // Aggiungiamo il blocco primario vuoto
        blocks.Add((Array.CreateInstance(typeof(byte), 0, 0), commonHeader));

        // 3. Preparazione estensioni
        for (int i = 0; i < sourceData.Count; i++)
        {
            var (pixels, originalHeader) = sourceData[i];

            // IMPORTANTE: Cloniamo l'header per non modificare l'oggetto in memoria
            var extHeader = _metadataService.CloneHeader(originalHeader);

            // Aggiungiamo metadati specifici per l'estensione
            _metadataService.SetValue(extHeader, "EXTNAME", $"FRAME_{i + 1}", "Extension Name");
            _metadataService.AddValue(extHeader, "HISTORY", $"Merged sequence frame - Compression: {settings.Compression}");
            
            blocks.Add((pixels, extHeader));
        }

        // 4. Scrittura fisica
        try
        {
            progress.Report(new BatchProgressReport(items.Count, items.Count, "Scrittura su disco...", 90));
            
            // Il servizio IO gestirà la compressione di ogni blocco se necessario
            await _ioService.WriteMergedFileAsync(finalPath, blocks, settings.Compression);
            
            foreach (var item in items.Where(x => !x.IsError))
            {
                item.Status = settings.Compression != FitsCompressionMode.None ? "Incluso (Compresso)" : "Incluso";
                item.IsSuccess = true;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Critical Merge IO Error: {ex.Message}");
            throw; // Rilancia per gestire l'errore UI a livello superiore se serve
        }
    }

    private async Task ExportSingleFilesAsync(
        List<ExportableItem> items,
        ExportJobSettings settings,
        IProgress<BatchProgressReport> progress,
        CancellationToken token)
    {
        for (int i = 0; i < items.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var item = items[i];
            progress.Report(new BatchProgressReport(i + 1, items.Count, item.FileName, (double)i / items.Count * 100));

            try
            {
                string extension = GetExtension(settings.Format);
                string outName = Path.GetFileNameWithoutExtension(item.FileName);
                if (!string.IsNullOrWhiteSpace(settings.BaseFileName))
                    outName = $"{settings.BaseFileName}_{i + 1:D3}";
                
                string outPath = Path.Combine(settings.OutputDirectory, outName + extension);

                var package = await _dataManager.GetDataAsync(item.FullPath);
                var (pixels, originalHeader) = ExtractPrimaryData(package);

                if (pixels == null || originalHeader == null) throw new InvalidDataException("Dati mancanti o corrotti.");

                if (settings.Format == ExportFormat.Fits)
                {
                    // 1. CLONAZIONE HEADER
                    // Evita effetti collaterali se l'immagine è aperta nel visualizzatore
                    var exportHeader = _metadataService.CloneHeader(originalHeader);

                    // 2. METADATI SEMANTICI
                    _metadataService.AddValue(exportHeader, "HISTORY", $"Exported by KomaLab on {DateTime.Now:O}");
                    _metadataService.AddValue(exportHeader, "HISTORY", $"Compression Mode: {settings.Compression}");
                    _metadataService.SetValue(exportHeader, "CREATOR", "KomaLab Processing Suite", "Software name");

                    // 3. SCRITTURA
                    // Passiamo l'header standard. Se la compressione è attiva, FitsIoService 
                    // chiamerà FitsCompression che trasformerà questo header in BINTABLE.
                    await _ioService.WriteFileAsync(outPath, pixels, exportHeader, settings.Compression);
                }
                else
                {
                    // Export Bitmap (PNG/JPG)
                    var absoluteProfile = settings.Profile as AbsoluteContrastProfile;
                    await Task.Run(() =>
                    {
                        _bitmapService.ExportBitmap(pixels, originalHeader, outPath, settings.Format, settings.JpegQuality, absoluteProfile);
                    }, token);
                }

                item.Status = settings.Compression != FitsCompressionMode.None && settings.Format == ExportFormat.Fits 
                    ? "Salvato (Compresso)" : "Salvato";
                item.IsSuccess = true;
            }
            catch (Exception ex)
            {
                item.Status = $"Errore: {ex.Message}";
                item.IsError = true;
                Debug.WriteLine($"Export Error on {item.FileName}: {ex}");
            }
        }
    }

    private FitsHeader CreateCommonHeader(List<FitsHeader> headers)
    {
        var common = new FitsHeader();
        
        // Header minimale per il Primary HDU di un file MEF
        _metadataService.SetValue(common, "SIMPLE", true, "Standard FITS format");
        _metadataService.SetValue(common, "BITPIX", 8, "No data in primary HDU");
        _metadataService.SetValue(common, "NAXIS", 0, "No data axes");
        _metadataService.SetValue(common, "EXTEND", true, "Extensions are permitted");

        // Tentativo di preservare metadati comuni (Telescopio, Oggetto, ecc.)
        string[] keysToSync = { "OBJECT", "TELESCOP", "INSTRUME", "OBSERVER", "SITENAME", "DATE-OBS" };
        foreach (var key in keysToSync)
        {
            string commonValue = GetCommonValue(headers, key);
            if (commonValue != null)
                _metadataService.SetValue(common, key, commonValue, "Common metadata");
        }
        return common;
    }

    private string? GetCommonValue(List<FitsHeader> headers, string key)
    {
        if (headers.Count == 0) return null;
        
        // Usa MetadataService per leggere in modo robusto (gestione commenti, apici, ecc.)
        var firstValue = _metadataService.GetStringValue(headers[0], key);
        if (string.IsNullOrEmpty(firstValue)) return null;

        bool isCommon = headers.All(h => _metadataService.GetStringValue(h, key) == firstValue);
        return isCommon ? firstValue : null;
    }

    private (Array? pixels, FitsHeader? header) ExtractPrimaryData(FitsDataPackage package)
    {
        // Logica per estrarre l'immagine principale (o dal primario o dalla prima estensione valida)
        var hdu = package.FirstImageHdu ?? package.PrimaryHdu;
        return (hdu?.PixelData, hdu?.Header);
    }

    private string GetExtension(ExportFormat format) => format switch
    {
        ExportFormat.Fits => ".fits",
        ExportFormat.Png => ".png",
        ExportFormat.Jpeg => ".jpg",
        _ => ".dat"
    };
}