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

        // 1. Caricamento e analisi preliminare
        for (int i = 0; i < items.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var item = items[i];
            progress.Report(new BatchProgressReport(i + 1, items.Count, $"Analisi: {item.FileName}", (double)i / items.Count * 50));

            try
            {
                var package = await _dataManager.GetDataAsync(item.FullPath);
                var (pixels, header) = ExtractPrimaryData(package);
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

        // 2. Creazione dell'HDU Primario "Null"
        var commonHeader = CreateCommonHeader(sourceData.Select(d => d.Header).ToList());
        _metadataService.AddValue(commonHeader, "HISTORY", $"KomaLab MEF Container created: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        var blocks = new List<(Array Pixels, FitsHeader Header)>();
        
        // Aggiungiamo il blocco primario vuoto (Null Primary non viene mai compresso)
        blocks.Add((Array.CreateInstance(typeof(byte), 0, 0), commonHeader));

        // 3. Aggiunta delle immagini come estensioni
        for (int i = 0; i < sourceData.Count; i++)
        {
            var (pixels, header) = sourceData[i];
            _metadataService.AddValue(header, "EXTNAME", $"FRAME_{i + 1}");
            _metadataService.AddValue(header, "HISTORY", $"Merged sequence frame - Compression: {settings.Compression}");
            
            blocks.Add((pixels, header));
        }

        // 4. Scrittura fisica tramite IO Service passando la modalità di compressione
        try
        {
            progress.Report(new BatchProgressReport(items.Count, items.Count, "Compressione e Scrittura MEF...", 90));
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
            throw;
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
                var (pixels, header) = ExtractPrimaryData(package);

                if (pixels == null || header == null) throw new InvalidDataException("Dati mancanti.");

                if (settings.Format == ExportFormat.Fits)
                {
                    _metadataService.AddValue(header, "HISTORY", $"KomaLab Export - Compression: {settings.Compression}");
                    // Passiamo il flag di compressione al servizio IO
                    await _ioService.WriteFileAsync(outPath, pixels, header, settings.Compression);
                }
                else
                {
                    var absoluteProfile = settings.Profile as AbsoluteContrastProfile;
                    await Task.Run(() =>
                    {
                        _bitmapService.ExportBitmap(pixels, header, outPath, settings.Format, settings.JpegQuality, absoluteProfile);
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
            }
        }
    }

    private FitsHeader CreateCommonHeader(List<FitsHeader> headers)
    {
        var common = new FitsHeader();
        common.AddCard(new FitsCard("BITPIX", "8", "No data", false));
        common.AddCard(new FitsCard("NAXIS", "0", "Null Primary HDU", false));
        common.AddCard(new FitsCard("EXTEND", "T", "File contains extensions", false));

        string[] keysToSync = { "OBJECT", "TELESCOP", "INSTRUME", "OBSERVER", "LOCATION", "DATE-OBS" };
        foreach (var key in keysToSync)
        {
            string commonValue = GetCommonValue(headers, key);
            if (commonValue != null)
                common.AddCard(new FitsCard(key, commonValue, "Common metadata", false));
        }
        return common;
    }

    private string? GetCommonValue(List<FitsHeader> headers, string key)
    {
        if (headers.Count == 0) return null;
        var firstValue = headers[0].Cards.FirstOrDefault(c => c.Key == key)?.Value;
        if (string.IsNullOrEmpty(firstValue)) return null;
        bool isCommon = headers.All(h => h.Cards.FirstOrDefault(c => c.Key == key)?.Value == firstValue);
        return isCommon ? firstValue : null;
    }

    private (Array? pixels, FitsHeader? header) ExtractPrimaryData(FitsDataPackage package)
    {
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