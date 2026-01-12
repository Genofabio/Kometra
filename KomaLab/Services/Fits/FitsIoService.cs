using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using KomaLab.Models.Fits;
using nom.tam.fits;
using nom.tam.util;

namespace KomaLab.Services.Fits;

// ---------------------------------------------------------------------------
// FILE: FitsIoService.cs
// RUOLO: I/O Scientifico e Coordinatore Batch
// DESCRIZIONE:
// Centralizza la lettura, scrittura e validazione dei file FITS.
// Dopo il refactoring, assorbe le logiche di FitsBatchService per:
// 1. Evitare classi ridondanti (Purezza Architetturale).
// 2. Mantenere una gerarchia di dipendenze lineare (IoService -> MetadataService).
// ---------------------------------------------------------------------------

public class FitsIoService : IFitsIoService
{
    private readonly IFileStreamProvider _streamProvider;
    private readonly IFitsMetadataService _metadataService;

    public FitsIoService(
        IFileStreamProvider streamProvider, 
        IFitsMetadataService metadataService)
    {
        _streamProvider = streamProvider ?? throw new ArgumentNullException(nameof(streamProvider));
        _metadataService = metadataService ?? throw new ArgumentNullException(nameof(metadataService));
    }

    // --- OPERAZIONI SU SINGOLO FILE ---

    public async Task<FitsImageData?> LoadAsync(string path)
    {
        return await Task.Run(() =>
        {
            try
            {
                using Stream stream = _streamProvider.Open(path);
                
                // Bufferizzazione necessaria per l'accesso random (Seek) richiesto da CSharpFits
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                ms.Position = 0;

                var fits = new nom.tam.fits.Fits(ms);
                fits.Read();

                ImageHDU? imgHdu = null;
                for (int i = 0; i < fits.NumberOfHDUs; i++)
                {
                    var hdu = fits.GetHDU(i);
                    if (hdu is ImageHDU im && im.Axes.Length >= 2)
                    {
                        imgHdu = im;
                        break;
                    }
                }

                if (imgHdu == null) return null;

                var rawData = imgHdu.Kernel as Array;
                if (rawData == null) return null;

                if (rawData.Rank == 1) Array.Reverse(rawData);

                return new FitsImageData
                {
                    RawData = rawData,
                    FitsHeader = imgHdu.Header,
                    Width = imgHdu.Header.GetIntValue("NAXIS1"),
                    Height = imgHdu.Header.GetIntValue("NAXIS2")
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[FitsIoService] Errore caricamento {path}: {ex.Message}");
                return null;
            }
        });
    }

    public async Task<Header?> ReadHeaderOnlyAsync(string path)
    {
        return await Task.Run(() =>
        {
            try
            {
                using var stream = _streamProvider.Open(path);
                var fits = new nom.tam.fits.Fits(stream);
                var hdu = fits.ReadHDU();
                return hdu?.Header;
            }
            catch 
            { 
                return null; 
            }
        });
    }

    public async Task SaveAsync(FitsImageData data, string path)
    {
        await Task.Run(() =>
        {
            if (data.RawData == null) throw new ArgumentException("Nessun dato da salvare.");

            var arrayToSave = (Array)data.RawData.Clone();
            if (arrayToSave.Rank == 1) Array.Reverse(arrayToSave);

            var hdu = FitsFactory.HDUFactory(arrayToSave);
            
            // Delega al cervello (MetadataService) la logica di filtraggio/trasferimento chiavi
            _metadataService.TransferMetadata(data.FitsHeader, hdu.Header);

            using var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite);
            using var bs = new BufferedDataStream(fs);
            
            var newFits = new nom.tam.fits.Fits();
            newFits.AddHDU(hdu);
            newFits.Write(bs);
            
            bs.Flush();
            fs.Flush();
        });
    }

    // --- OPERAZIONI BATCH (Ex FitsBatchService) ---

    /// <summary>
    /// Analizza un set di file e li restituisce ordinati cronologicamente.
    /// </summary>
    public async Task<List<string>> PrepareBatchAsync(IEnumerable<string> paths)
    {
        var pathList = paths.ToList();
        if (pathList.Count <= 1) return pathList;

        try
        {
            var tasks = pathList.Select(async path =>
            {
                var header = await ReadHeaderOnlyAsync(path);
                // Utilizza direttamente il MetadataService per interpretare l'header letto
                var date = header != null ? _metadataService.GetObservationDate(header) : DateTime.MinValue;
                return new { Path = path, Date = date ?? DateTime.MinValue };
            });

            var results = await Task.WhenAll(tasks);

            return results
                .OrderBy(x => x.Date)
                .Select(x => x.Path)
                .ToList();
        }
        catch
        {
            return pathList;
        }
    }

    /// <summary>
    /// Verifica se una sequenza di file è compatibile per operazioni di Stacking o Allineamento.
    /// </summary>
    public async Task<(bool IsCompatible, string? Error)> ValidateCompatibilityAsync(IEnumerable<string> paths)
    {
        int? firstWidth = null;
        int? firstHeight = null;

        foreach (var path in paths)
        {
            var header = await ReadHeaderOnlyAsync(path);
            if (header == null) continue;

            int w = header.GetIntValue("NAXIS1");
            int h = header.GetIntValue("NAXIS2");

            if (firstWidth == null)
            {
                firstWidth = w;
                firstHeight = h;
            }
            else if (w != firstWidth || h != firstHeight)
            {
                return (false, $"Dimensioni non corrispondenti. File: {System.IO.Path.GetFileName(path)}");
            }
        }

        return (true, null);
    }
}