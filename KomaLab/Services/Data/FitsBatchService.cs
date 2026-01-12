using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace KomaLab.Services.Data;

// ---------------------------------------------------------------------------
// FILE: FitsBatchService.cs
// RUOLO: Coordinatore di Processi Multi-File (The Orchestrator)
// DESCRIZIONE:
// Questo servizio si pone al vertice della gerarchia dei dati. Il suo compito è
// trasformare un insieme di percorsi file "grezzi" in una sequenza logica e validata.
//
// RESPONSABILITÀ:
// 1. Orchestrazione: Coordina FitsIoService (per la lettura) e FitsMetadataService 
//    (per l'estrazione date) senza che i due debbano conoscersi a vicenda per il riordino.
// 2. Business Logic di Insieme: Gestisce regole che non riguardano il singolo pixel, 
//    ma la relazione tra più immagini (es. ordinamento, coerenza dimensionale).
// 3. Validazione Batch: Filtra file corrotti o incompatibili prima che arrivino alla UI.
// 4. Semplificazione UI: Riduce la complessità dei ViewModel, offrendo metodi "pronti all'uso".
// ---------------------------------------------------------------------------

public class FitsBatchService : IFitsBatchService
{
    private readonly IFitsIoService _ioService;
    private readonly IFitsMetadataService _metadataService;

    public FitsBatchService(IFitsIoService ioService, IFitsMetadataService metadataService)
    {
        _ioService = ioService ?? throw new ArgumentNullException(nameof(ioService));
        _metadataService = metadataService ?? throw new ArgumentNullException(nameof(metadataService));
    }

    /// <summary>
    /// Prende una lista di percorsi, ne legge gli header in parallelo e li restituisce
    /// ordinati per data di osservazione. Gestisce anche il caso di file singolo.
    /// </summary>
    public async Task<List<string>> PrepareBatchAsync(IEnumerable<string> paths)
    {
        var pathList = paths.ToList();
        if (pathList.Count <= 1) return pathList;

        try
        {
            // Esecuzione parallela della lettura degli header per massimizzare le prestazioni
            var tasks = pathList.Select(async path =>
            {
                var header = await _ioService.ReadHeaderOnlyAsync(path);
                // Se l'header non è leggibile o la data manca, usiamo MinValue per l'ordinamento
                var date = header != null ? _metadataService.GetObservationDate(header) : DateTime.MinValue;
                return new { Path = path, Date = date ?? DateTime.MinValue };
            });

            var results = await Task.WhenAll(tasks);

            // Restituisce i percorsi ordinati cronologicamente
            return results
                .OrderBy(x => x.Date)
                .Select(x => x.Path)
                .ToList();
        }
        catch (Exception)
        {
            // In caso di errore critico, restituiamo la lista originale non ordinata
            return pathList;
        }
    }

    /// <summary>
    /// Controlla se tutte le immagini hanno la stessa risoluzione (Width/Height).
    /// Essenziale prima di procedere con Stacking o Allineamento.
    /// </summary>
    public async Task<(bool IsCompatible, string? Error)> ValidateCompatibilityAsync(IEnumerable<string> paths)
    {
        int? firstWidth = null;
        int? firstHeight = null;

        foreach (var path in paths)
        {
            var header = await _ioService.ReadHeaderOnlyAsync(path);
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