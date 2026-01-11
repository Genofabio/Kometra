using System;
using System.IO;
using System.Threading.Tasks;
using KomaLab.Models.Fits;
using nom.tam.fits;
using nom.tam.util;

namespace KomaLab.Services.Data;

// ---------------------------------------------------------------------------
// FILE: FitsIoService.cs
// RUOLO: I/O Scientifico
// DESCRIZIONE:
// Gestisce esclusivamente la lettura e scrittura fisica dei file FITS su disco.
// Orchestratala creazione degli oggetti FitsImageData delegando:
// - La gestione dei metadati a IFitsMetadataService.
// - L'apertura dei flussi a IFileStreamProvider.
//
// NOTA:
// Questo servizio preserva l'integrità scientifica dei dati (non converte a 8-bit,
// non applica stretch distruttivi). Per l'export visuale, vedi MediaExportService.
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

    public async Task<FitsImageData?> LoadAsync(string path)
    {
        return await Task.Run(() =>
        {
            try
            {
                // 1. Apertura stream astratto (File Locale o Risorsa Embedded)
                using Stream stream = _streamProvider.Open(path);
                
                // 2. Bufferizzazione in memoria
                // Necessario perché la libreria CSharpFits richiede accesso random (Seek)
                // e per evitare lock prolungati sul file fisico.
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                ms.Position = 0;

                var fits = new Fits(ms);
                fits.Read();

                // 3. Ricerca della prima immagine valida
                // Alcuni FITS hanno l'HDU primario vuoto e i dati nella prima estensione.
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

                // 4. Estrazione Array Raw
                var rawData = imgHdu.Kernel as Array;
                if (rawData == null) return null;

                // FIX: CSharpFits talvolta legge array 1D invertiti rispetto allo standard
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
                // In produzione qui andrebbe un Logger.LogError(...)
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
                var fits = new Fits(stream);
                
                // Ottimizzazione: Legge SOLO il primo Header Unit (Primary HDU).
                // Solitamente contiene i metadati globali (Data, Oggetto, Telescopio).
                // Non carica i dati immagine, rendendolo rapidissimo per scansioni di directory.
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

            // 1. Clonazione difensiva dei dati
            // Evita che modifiche successive all'array in memoria corrompano il salvataggio
            // o che il processo di salvataggio modifichi i dati originali (es. reverse).
            var arrayToSave = (Array)data.RawData.Clone();
            if (arrayToSave.Rank == 1) Array.Reverse(arrayToSave);

            // 2. Creazione HDU FITS
            // FitsFactory calcola automaticamente BITPIX e NAXIS corretti basandosi sul tipo di array C#
            var hdu = FitsFactory.HDUFactory(arrayToSave);
            
            // 3. Trasferimento Metadati (Punto chiave Architetturale)
            // Usiamo il servizio dedicato per copiare i metadati astronomici dal vecchio header
            // al nuovo, filtrando automaticamente le chiavi tecniche obsolete.
            _metadataService.TransferMetadata(data.FitsHeader, hdu.Header);

            // 4. Scrittura fisica
            // Usiamo FileStream diretto perché il salvataggio è sempre un output locale
            using var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite);
            using var bs = new BufferedDataStream(fs);
            
            var newFits = new Fits();
            newFits.AddHDU(hdu);
            newFits.Write(bs);
            
            bs.Flush();
            fs.Flush();
        });
    }
}