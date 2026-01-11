using System;
using nom.tam.fits;
using KomaLab.Models.Astrometry;

namespace KomaLab.Services.Data;

public interface IFitsMetadataService
{
    /// <summary>
    /// Copia i metadati da un header sorgente a uno destinazione, 
    /// filtrando automaticamente le chiavi tecniche (BITPIX, NAXIS...) che non devono essere copiate.
    /// </summary>
    void TransferMetadata(Header source, Header destination);

    /// <summary>
    /// Crea un nuovo header pulito e basilare, copiando poi i metadati informativi dal sorgente.
    /// </summary>
    Header CloneAndSanitize(Header source);

    /// <summary>
    /// Estrae i dati WCS normalizzati utilizzando il WcsParser.
    /// </summary>
    WcsData ExtractWcs(Header header);

    /// <summary>
    /// Tenta di estrarre la data di osservazione standardizzando i vari formati FITS.
    /// </summary>
    DateTime? GetObservationDate(Header header);
}