using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using KomaLab.Models.Astrometry;
using KomaLab.Models.Fits;
using KomaLab.Services.Fits.Parsers;
using KomaLab.ViewModels.Items;

namespace KomaLab.Services.Fits;

public class FitsMetadataService : IFitsMetadataService
{
    // Chiavi che definiscono la struttura fisica e NON devono essere copiate dal template
    // perché dipendono dai nuovi pixel generati.
    private static readonly HashSet<string> StructuralKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "SIMPLE", "BITPIX", "NAXIS", "NAXIS1", "NAXIS2", "NAXIS3", 
        "EXTEND", "PCOUNT", "GCOUNT", "GROUPS",
        "BSCALE", "BZERO", "DATAMIN", "DATAMAX",
        "CHECKSUM", "DATASUM" 
    };

    // =======================================================================
    // NUOVO METODO: Creazione Header Ibrido
    // =======================================================================

    public FitsHeader CreateHeaderFromTemplate(FitsHeader template, Array newPixels, FitsBitDepth depth)
    {
        var newHeader = new FitsHeader();

        // 1. Scriviamo le chiavi tecniche basate sui NUOVI pixel
        newHeader.Add("SIMPLE", true, "Standard FITS format");
        newHeader.Add("BITPIX", (int)depth, GetBitpixComment((int)depth));
        newHeader.Add("NAXIS", 2, "2D Image");
        // Nota: GetLength(1) è la larghezza (colonne), GetLength(0) è l'altezza (righe)
        newHeader.Add("NAXIS1", newPixels.GetLength(1), "Image Width");
        newHeader.Add("NAXIS2", newPixels.GetLength(0), "Image Height");
        
        // Impostiamo scaling standard (i dati salvati sono già fisici o normalizzati)
        newHeader.Add("BSCALE", 1.0, "Physical = Raw * BSCALE + BZERO");
        newHeader.Add("BZERO", 0.0, "No Offset");

        // 2. Copiamo tutto il resto (Telescopio, Oggetto, Coordinate, Storia) dal vecchio header
        if (template != null)
        {
            TransferMetadata(source: template, destination: newHeader);
            newHeader.Add("HISTORY", null, "Processed with KomaLab");
        }
        else
        {
            newHeader.AddCard(new FitsCard { Key = "END", IsCommentStyle = true });
        }

        return newHeader;
    }

    // =======================================================================
    // Metodi Esistenti (Logic Transfer)
    // =======================================================================

    public void TransferMetadata(FitsHeader source, FitsHeader destination)
    {
        if (source == null || destination == null) return;

        foreach (var card in source.Cards)
        {
            // Saltiamo le chiavi strutturali perché sono già state scritte corrette per la nuova immagine
            if (StructuralKeys.Contains(card.Key) || card.Key == "END") continue;
            
            // Aggiungiamo una copia della card
            destination.AddCard(card.Clone());
        }
    }

    // --- Parser Delegati (Invariati) ---

    public WcsData ExtractWcs(FitsHeader header) => WcsParser.Parse(header);
    public GeographicLocation? GetObservatoryLocation(FitsHeader header) => GeographicParser.ParseLocation(header);
    public double? GetFocalLength(FitsHeader header) => OpticalParser.ParseFocalLength(header);
    public double? GetPixelSize(FitsHeader header) => OpticalParser.ParsePixelSize(header);

    public DateTime? GetObservationDate(FitsHeader header)
    {
        string dateStr = header.GetStringValue("DATE-OBS");
        if (string.IsNullOrWhiteSpace(dateStr)) dateStr = header.GetStringValue("DATE");
        
        if (string.IsNullOrWhiteSpace(dateStr)) return null;

        if (DateTime.TryParse(dateStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime dt))
        {
            return dt;
        }
        return null;
    }

    // --- Editor Logic (Invariata) ---

    public List<FitsHeaderEditorRow> ParseForEditor(FitsHeader header)
    {
        var result = new List<FitsHeaderEditorRow>();
        foreach (var card in header.Cards)
        {
            bool isReadOnly = StructuralKeys.Contains(card.Key);
            result.Add(new FitsHeaderEditorRow(
                key: card.Key,
                value: card.Value ?? "",
                comment: card.Comment ?? "",
                isReadOnly: isReadOnly
            ));
        }
        return result;
    }

    public FitsHeader ReconstructHeader(FitsHeader originalHeader, IEnumerable<FitsHeaderEditorRow> editedItems)
    {
        var newHeader = new FitsHeader();
        var originalMap = originalHeader.Cards
            .GroupBy(c => c.Key) 
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var item in editedItems)
        {
            string key = item.Key?.Trim().ToUpper() ?? "";
            if (string.IsNullOrWhiteSpace(key) || key == "END") continue;

            if (StructuralKeys.Contains(key) && originalMap.ContainsKey(key))
            {
                newHeader.AddCard(originalMap[key]);
            }
            else
            {
                var newCard = new FitsCard
                {
                    Key = key,
                    Value = item.Value,
                    Comment = item.Comment,
                    IsCommentStyle = (key == "HISTORY" || key == "COMMENT")
                };
                newHeader.AddCard(newCard);
            }
        }
        newHeader.AddCard(new FitsCard { Key = "END", IsCommentStyle = true });
        return newHeader;
    }

    private string GetBitpixComment(int bitpix) => bitpix switch
    {
        8 => "8-bit Unsigned Integer",
        16 => "16-bit Integer",
        32 => "32-bit Integer",
        -32 => "Single Precision Float",
        -64 => "Double Precision Float",
        _ => ""
    };
}