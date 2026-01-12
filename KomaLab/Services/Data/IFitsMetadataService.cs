using System;
using System.Collections.Generic;
using nom.tam.fits;
using KomaLab.Models.Astrometry;
using KomaLab.Models.Fits;

namespace KomaLab.Services.Data;

public interface IFitsMetadataService
{
    
    // --- Metodi di Base ---
    
    /// <summary>
    /// Copia i metadati da un header sorgente a uno destinazione, 
    /// filtrando automaticamente le chiavi tecniche (BITPIX, NAXIS...) che non devono essere copiate.
    /// </summary>
    void TransferMetadata(Header source, Header destination);

    /// <summary>
    /// Crea un nuovo header pulito e basilare, copiando poi i metadati informativi dal sorgente.
    /// </summary>
    Header CloneAndSanitize(Header source);
    
    // --- Metodi di Estrazione Dati (Delegati ai Parser) ---

    /// <summary>
    /// Estrae i dati WCS normalizzati utilizzando il WcsParser.
    /// </summary>
    WcsData ExtractWcs(Header header);

    /// <summary>
    /// Tenta di estrarre la data di osservazione standardizzando i vari formati FITS.
    /// </summary>
    DateTime? GetObservationDate(Header header);
    
    // --- Metodi per l'Editor ---

    /// <summary>
    /// Parsa l'header in una lista piatta di oggetti UI-friendly, gestendo HIERARCH e chiavi di sistema.
    /// </summary>
    List<FitsHeaderItem> ParseForEditor(Header header);

    /// <summary>
    /// Ricostruisce un Header valido combinando le chiavi strutturali dell'originale
    /// con le modifiche apportate dall'utente.
    /// </summary>
    Header ReconstructHeader(Header originalHeader, IEnumerable<FitsHeaderItem> editedItems);
}
