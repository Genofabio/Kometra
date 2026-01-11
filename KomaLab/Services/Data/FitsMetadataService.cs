using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using nom.tam.fits;
using KomaLab.Models.Astrometry;
using KomaLab.Services.Data.Parsers;

namespace KomaLab.Services.Data;

// ---------------------------------------------------------------------------
// FILE: FitsMetadataService.cs
// RUOLO: Gestore Metadati (The Brain)
// DESCRIZIONE:
// Punto centrale per la manipolazione logica degli Header FITS.
// Responsabilità:
// 1. Sanitizzazione: Sa quali chiavi tecniche rimuovere durante copie/clonazioni.
// 2. Orchestrazione: Coordina i Parser specifici (WCS, Geo) per estrarre DTO.
// 3. Astrazione: Nasconde la complessità di iterazione di CSharpFits (Cursor/DictionaryEntry).
// ---------------------------------------------------------------------------

public class FitsMetadataService : IFitsMetadataService
{
    // BLACKLIST: Chiavi strutturali che dipendono dal binario e non devono essere copiate "alla cieca".
    // Queste chiavi vengono rigenerate automaticamente dal writer in base ai dati fisici.
    private static readonly HashSet<string> StructuralKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "SIMPLE", "BITPIX", "NAXIS", "NAXIS1", "NAXIS2", "NAXIS3", 
        "EXTEND", "PCOUNT", "GCOUNT", "GROUPS",
        "BSCALE", "BZERO",       // Dipende dal tipo dati (Int vs Float)
        "DATAMIN", "DATAMAX",    // Dipende dai valori dei pixel
        "CHECKSUM", "DATASUM",   // Invalidati da qualsiasi modifica ai dati
        "END"
    };

    public void TransferMetadata(Header source, Header destination)
    {
        var cursor = source.GetCursor();
        while (cursor.MoveNext())
        {
            HeaderCard? card = null;

            // Gestione robusta per recuperare le card da CSharpFits (Legacy compatibility)
            // La libreria a volte espone le card come DictionaryEntry, altre come HeaderCard dirette.
            if (cursor.Current is DictionaryEntry de && de.Value is HeaderCard hc) 
                card = hc;
            else if (cursor.Current is HeaderCard c) 
                card = c;

            if (card == null) continue;

            // Filtro Blacklist: Se è una chiave tecnica, la ignoriamo.
            string key = card.Key?.Trim().ToUpper() ?? "";
            if (StructuralKeys.Contains(key)) continue;

            // Aggiungiamo la card alla destinazione.
            // Il try-catch è necessario perché FITS malformati possono contenere caratteri illegali
            // nei commenti che farebbero crashare il salvataggio. Meglio perdere un commento che il file.
            try 
            { 
                destination.AddCard(card); 
            } 
            catch 
            { 
                // Log opzionale: Debug.WriteLine($"Skipped malformed card: {key}");
            }
        }
    }

    public Header CloneAndSanitize(Header source)
    {
        var newHeader = new Header();
        
        // Imposta valori base minimi per avere un header valido in memoria.
        // Nota: BITPIX -64 riflette lo standard interno dell'app (Double Precision).
        newHeader.AddValue("SIMPLE", true, "Standard FITS format");
        newHeader.AddValue("BITPIX", -64, "Double Precision"); 
        newHeader.AddValue("NAXIS", 2, "2D Image");
        
        TransferMetadata(source, newHeader);
        
        return newHeader;
    }

    public WcsData ExtractWcs(Header header)
    {
        // Delega al Parser statico specializzato
        return WcsParser.Parse(header);
    }
    
    public GeographicLocation? GetObservatoryLocation(Header header)
    {
        // Delega al Parser statico specializzato
        return GeographicParser.ParseLocation(header);
    }

    public DateTime? GetObservationDate(Header header)
    {
        // Logica di fallback standard FITS
        string? dateStr = header.GetStringValue("DATE-OBS");
        
        // Alcuni software usano solo DATE
        if (string.IsNullOrWhiteSpace(dateStr)) dateStr = header.GetStringValue("DATE");
        
        if (string.IsNullOrWhiteSpace(dateStr)) return null;

        // Tentativo di parsing ISO-8601 subset (Standard FITS)
        if (DateTime.TryParse(dateStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime dt))
        {
            return dt;
        }
        return null;
    }
}