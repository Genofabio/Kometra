using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kometra.Models.Export;
using Kometra.Models.Fits.Structure;
using Kometra.Models.Processing;
using Kometra.Models.Processing.Batch;
using Kometra.Models.Visualization;
using Kometra.Services.Fits;
using Kometra.Services.Fits.IO;
using Kometra.Services.Fits.Metadata;

namespace Kometra.Services.ImportExport;

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
        // 1. Calcolo nome file corretto con estensione .fits o .fits.fz
        string baseName = !string.IsNullOrEmpty(settings.BaseFileName) ? settings.BaseFileName : "Merged_Sequence";
        
        // Puliamo eventuali estensioni digitate dall'utente per evitare "nome.fits.fits.fz"
        if (baseName.EndsWith(".fits", StringComparison.OrdinalIgnoreCase))
            baseName = Path.GetFileNameWithoutExtension(baseName);
        if (baseName.EndsWith(".fz", StringComparison.OrdinalIgnoreCase)) // Caso raro nome.fits.fz
            baseName = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(baseName));

        string extension = GetExtension(settings.Format, settings.Compression);
        string finalPath = Path.Combine(settings.OutputDirectory, baseName + extension);

        var sourceData = new List<(Array Pixels, FitsHeader Header)>();

        // 2. Caricamento dati
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

        // 3. Creazione dell'HDU Primario "Null"
        var commonHeader = CreateCommonHeader(sourceData.Select(d => d.Header).ToList());
        _metadataService.AddValue(commonHeader, "CREATOR", "Kometra");

        var blocks = new List<(Array Pixels, FitsHeader Header)>();
        blocks.Add((Array.CreateInstance(typeof(byte), 0, 0), commonHeader));

        // 4. Preparazione estensioni
        for (int i = 0; i < sourceData.Count; i++)
        {
            var (pixels, originalHeader) = sourceData[i];
            var extHeader = _metadataService.CloneHeader(originalHeader);

            _metadataService.SetValue(extHeader, "EXTNAME", $"FRAME_{i + 1}", "Extension Name");
            _metadataService.AddValue(extHeader, "HISTORY", $"Kometra - MEF Compression Mode: {settings.Compression}");
            
            blocks.Add((pixels, extHeader));
        }

        // 5. Scrittura fisica
        try
        {
            progress.Report(new BatchProgressReport(items.Count, items.Count, "Scrittura su disco...", 90));
            
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
                // CORREZIONE: Qui passiamo anche la Compression al metodo GetExtension
                string extension = GetExtension(settings.Format, settings.Compression);
                
                string outName = Path.GetFileNameWithoutExtension(item.FileName);
                if (!string.IsNullOrWhiteSpace(settings.BaseFileName))
                    outName = $"{settings.BaseFileName}_{i + 1:D3}";
                
                string outPath = Path.Combine(settings.OutputDirectory, outName + extension);

                var package = await _dataManager.GetDataAsync(item.FullPath);
                var (pixels, originalHeader) = ExtractPrimaryData(package);

                if (pixels == null || originalHeader == null) throw new InvalidDataException("Dati mancanti o corrotti.");

                if (settings.Format == ExportFormat.Fits)
                {
                    var exportHeader = _metadataService.CloneHeader(originalHeader);
                    _metadataService.AddValue(exportHeader, "HISTORY", $"Kometra - Compression Mode: {settings.Compression}");
                    _metadataService.SetValue(exportHeader, "CREATOR", "Kometra", "Software name");

                    await _ioService.WriteFileAsync(outPath, pixels, exportHeader, settings.Compression);
                }
                else
                {
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
        _metadataService.SetValue(common, "SIMPLE", true, "Standard FITS format");
        _metadataService.SetValue(common, "BITPIX", 8, "No data in primary HDU");
        _metadataService.SetValue(common, "NAXIS", 0, "No data axes");
        _metadataService.SetValue(common, "EXTEND", true, "Extensions are permitted");

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
        var firstValue = _metadataService.GetStringValue(headers[0], key);
        if (string.IsNullOrEmpty(firstValue)) return null;
        bool isCommon = headers.All(h => _metadataService.GetStringValue(h, key) == firstValue);
        return isCommon ? firstValue : null;
    }

    private (Array? pixels, FitsHeader? header) ExtractPrimaryData(FitsDataPackage package)
    {
        var hdu = package.FirstImageHdu ?? package.PrimaryHdu;
        return (hdu?.PixelData, hdu?.Header);
    }

    // =======================================================================
    // FIX: LOGICA ESTENSIONE CORRETTA
    // =======================================================================
    private string GetExtension(ExportFormat format, FitsCompressionMode compression = FitsCompressionMode.None)
    {
        if (format == ExportFormat.Fits)
        {
            // Se c'è compressione attiva, l'estensione standard è .fits.fz
            if (compression != FitsCompressionMode.None) return ".fits.fz";
            return ".fits";
        }

        return format switch
        {
            ExportFormat.PNG => ".png",
            ExportFormat.JPEG => ".jpg",
            _ => ".dat"
        };
    }
}