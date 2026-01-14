using System;
using System.Collections.Generic;
using KomaLab.Models.Astrometry;
using KomaLab.Models.Fits;
using KomaLab.ViewModels.Items;

namespace KomaLab.Services.Fits;

public interface IFitsMetadataService
{
    
    // --- Metodi di Base ---
    
    /// <summary>
    /// Copia i metadati da un header sorgente a uno destinazione, 
    /// filtrando automaticamente le chiavi tecniche (BITPIX, NAXIS...) che non devono essere copiate.
    /// </summary>
    void TransferMetadata(FitsHeader source, FitsHeader destination);
    
    // --- Metodi di Estrazione Dati (Delegati ai Parser) ---

    /// <summary>
    /// Estrae i dati WCS normalizzati utilizzando il WcsParser.
    /// </summary>
    WcsData ExtractWcs(FitsHeader header);

    /// <summary>
    /// Estrae le coordinate geografiche dell'osservatorio (Lat/Lon).
    /// </summary>
    GeographicLocation? GetObservatoryLocation(FitsHeader header);

    /// <summary>
    /// Tenta di estrarre la data di osservazione standardizzando i vari formati FITS.
    /// </summary>
    DateTime? GetObservationDate(FitsHeader header);
    
    // --- Metodi per l'Editor ---

    /// <summary>
    /// Parsa l'header in una lista piatta di oggetti UI-friendly, gestendo HIERARCH e chiavi di sistema.
    /// </summary>
    List<FitsHeaderEditorRow> ParseForEditor(FitsHeader header);

    /// <summary>
    /// Ricostruisce un Header valido combinando le chiavi strutturali dell'originale
    /// con le modifiche apportate dall'utente.
    /// </summary>
    FitsHeader ReconstructHeader(FitsHeader originalHeader, IEnumerable<FitsHeaderEditorRow> editedItems);
}
