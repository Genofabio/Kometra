using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using KomaLab.Models.Astrometry;
using KomaLab.Models.Fits;
using KomaLab.Services.Fits.Parsers;
using nom.tam.fits;

namespace KomaLab.Services.Fits;

// ---------------------------------------------------------------------------
// FILE: FitsMetadataService.cs
// RUOLO: Gestore Centrale dei Metadati FITS (The Brain)
// DESCRIZIONE:
// Implementazione del servizio che centralizza tutta la logica di manipolazione degli Header.
//
// RESPONSABILITÀ:
// 1. Editor Support: Converte gli Header FITS in oggetti modificabili (FitsHeaderItem) e viceversa,
//    gestendo la complessità delle chiavi HIERARCH e dei commenti (logica spostata dal ViewModel).
// 2. Integrità Dati: Mantiene una "Blacklist" (StructuralKeys) per proteggere le chiavi critiche
//    (es. NAXIS, BITPIX) da modifiche accidentali che corromperebbero la struttura del file.
// 3. Orchestrazione: Coordina i parser specifici (WcsParser, GeographicParser) per l'estrazione dati.
// 4. Astrazione: Maschera le idiosincrasie della libreria CSharpFits (es. iteratori misti).
// ---------------------------------------------------------------------------

public class FitsMetadataService : IFitsMetadataService
{
    // BLACKLIST: Chiavi che non devono essere toccate dall'editor né copiate ciecamente.
    private static readonly HashSet<string> StructuralKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "SIMPLE", "BITPIX", "NAXIS", "NAXIS1", "NAXIS2", "NAXIS3", 
        "EXTEND", "PCOUNT", "GCOUNT", "GROUPS",
        "BSCALE", "BZERO", "DATAMIN", "DATAMAX",
        "CHECKSUM", "DATASUM", "END"
    };

    // --- IMPLEMENTAZIONE METODI EDITOR (LOGICA SPOSTATA DAL VIEWMODEL) ---

    public List<FitsHeaderItem> ParseForEditor(Header header)
    {
        var result = new List<FitsHeaderItem>();
        var cursor = header.GetCursor();

        while (cursor.MoveNext())
        {
            HeaderCard? card = GetSafeCard(cursor.Current);
            if (card == null || string.IsNullOrWhiteSpace(card.Key)) continue;

            string key = card.Key.Trim();

            // Gestione HIERARCH
            if (key.ToUpper() == "HIERARCH")
            {
                var item = ParseHierarchCard(card);
                if (item != null) result.Add(item);
            }
            else
            {
                // Chiave Standard
                string displayKey = key.Replace(".", " "); // Normalizzazione visiva
                bool isReadOnly = StructuralKeys.Contains(displayKey);

                result.Add(new FitsHeaderItem
                {
                    Key = displayKey,
                    Value = card.Value ?? "",
                    Comment = card.Comment ?? "",
                    IsReadOnly = isReadOnly,
                    IsModified = false
                });
            }
        }
        return result;
    }

    public Header ReconstructHeader(Header originalHeader, IEnumerable<FitsHeaderItem> editedItems)
    {
        var newHeader = new Header();

        // 1. Copia le chiavi strutturali dall'originale (Fondamentale per non corrompere il FITS)
        // Il ViewModel non sa nulla di NAXIS, quindi ci fidiamo del file originale.
        var cursor = originalHeader.GetCursor();
        while (cursor.MoveNext())
        {
            HeaderCard? card = GetSafeCard(cursor.Current);
            if (card != null && StructuralKeys.Contains(card.Key))
            {
                newHeader.AddCard(card);
            }
        }

        // 2. Applica le modifiche dell'utente
        foreach (var item in editedItems)
        {
            // Sicurezza: Ignoriamo chiavi strutturali se l'utente ha provato a inserirle manualmente
            if (string.IsNullOrWhiteSpace(item.Key) || StructuralKeys.Contains(item.Key)) continue;

            try
            {
                string keyUpper = item.Key.Trim().ToUpper();
                if (keyUpper == "END") continue; // END viene gestito da CSharpFITS automaticamente

                // Gestione Commenti Liberi
                if (keyUpper == "COMMENT" || keyUpper == "HISTORY")
                {
                    string text = $"{item.Value} {item.Comment}".Trim();
                    newHeader.AddCard(new HeaderCard(keyUpper, null, text));
                    continue;
                }

                // Ricostruzione Chiave (Rimuove prefisso visuale HIERARCH se presente)
                string effectiveKey = item.Key;
                if (keyUpper.StartsWith("HIERARCH ")) effectiveKey = item.Key.Substring(9).Trim();

                // Tentativo di preservare i tipi di dato (Double, Bool o String)
                if (double.TryParse(item.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double dVal))
                    newHeader.AddValue(effectiveKey, dVal, item.Comment);
                else if (bool.TryParse(item.Value, out bool bVal))
                    newHeader.AddValue(effectiveKey, bVal, item.Comment);
                else
                    newHeader.AddValue(effectiveKey, item.Value ?? "", item.Comment);
            }
            catch 
            { 
                // Skip chiavi malformate per evitare crash durante il salvataggio
            }
        }
        
        return newHeader;
    }

    // --- METODI ESISTENTI (Orchestrator) ---

    public void TransferMetadata(Header source, Header destination)
    {
        var cursor = source.GetCursor();
        while (cursor.MoveNext())
        {
            HeaderCard? card = GetSafeCard(cursor.Current);
            if (card == null) continue;

            string key = card.Key?.Trim().ToUpper() ?? "";
            if (StructuralKeys.Contains(key)) continue;

            try { destination.AddCard(card); } catch { }
        }
    }

    public Header CloneAndSanitize(Header source)
    {
        var newHeader = new Header();
        newHeader.AddValue("SIMPLE", true, "Standard FITS format");
        newHeader.AddValue("BITPIX", -64, "Double Precision"); 
        newHeader.AddValue("NAXIS", 2, "2D Image");
        TransferMetadata(source, newHeader);
        return newHeader;
    }

    // Delega ai Parser statici (Strategy Pattern implicito)
    public WcsData ExtractWcs(Header header) => WcsParser.Parse(header);
    
    public GeographicLocation? GetObservatoryLocation(Header header) => GeographicParser.ParseLocation(header);

    public DateTime? GetObservationDate(Header header)
    {
        string? dateStr = header.GetStringValue("DATE-OBS");
        if (string.IsNullOrWhiteSpace(dateStr)) dateStr = header.GetStringValue("DATE");
        
        if (string.IsNullOrWhiteSpace(dateStr)) return null;

        if (DateTime.TryParse(dateStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime dt))
        {
            return dt;
        }
        return null;
    }

    // --- HELPER PRIVATI ---

    // Risolve il problema di CSharpFITS che a volte ritorna DictionaryEntry
    private HeaderCard? GetSafeCard(object current)
    {
        if (current is HeaderCard hc) return hc;
        if (current is DictionaryEntry de && de.Value is HeaderCard hcd) return hcd;
        return null;
    }

    private FitsHeaderItem? ParseHierarchCard(HeaderCard card)
    {
        string raw = card.ToString();
        int eqIndex = raw.IndexOf('=');
        if (eqIndex <= 8) return null;

        string realKey = raw.Substring(0, eqIndex).Trim();
        // FITS Standard: HIERARCH keys hanno spazio all'8° char
        if (realKey.Length > 8 && !char.IsWhiteSpace(realKey[8]))
            realKey = realKey.Insert(8, " ");

        string valPart = raw.Substring(eqIndex + 1).Trim();
        string realValue = valPart;
        string realComment = "";

        int slashIndex = valPart.IndexOf('/');
        if (slashIndex >= 0)
        {
            realValue = valPart.Substring(0, slashIndex).Trim();
            realComment = valPart.Substring(slashIndex + 1).Trim();
        }
        realValue = realValue.Replace("'", "");

        return new FitsHeaderItem
        {
            Key = realKey,
            Value = realValue,
            Comment = realComment,
            IsReadOnly = false, // HIERARCH è custom, quindi modificabile
            IsModified = false
        };
    }
}