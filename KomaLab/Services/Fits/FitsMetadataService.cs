using System;
using System.Collections.Generic;
using System.Globalization;
using KomaLab.Models.Astrometry;
using KomaLab.Models.Fits;            // <--- Namespace Core appiattito
using KomaLab.Services.Fits.Parsers;
using KomaLab.ViewModels.Items;

namespace KomaLab.Services.Fits;

public class FitsMetadataService : IFitsMetadataService
{
    private static readonly HashSet<string> StructuralKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "SIMPLE", "BITPIX", "NAXIS", "NAXIS1", "NAXIS2", "NAXIS3", 
        "EXTEND", "PCOUNT", "GCOUNT", "GROUPS",
        "BSCALE", "BZERO", "DATAMIN", "DATAMAX",
        "CHECKSUM", "DATASUM", "END"
    };

    // --- EDITOR UI ---

    public List<FitsHeaderEditorRow> ParseForEditor(FitsHeader header)
    {
        var result = new List<FitsHeaderEditorRow>();

        foreach (var card in header.Cards)
        {
            if (card.Key == "END") continue;

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

        // 1. Copia le chiavi strutturali dall'originale (Preserva integrità fisica)
        foreach (var card in originalHeader.Cards)
        {
            if (StructuralKeys.Contains(card.Key))
            {
                newHeader.AddCard(card);
            }
        }

        // 2. Aggiunge le righe modificate dall'utente
        foreach (var item in editedItems)
        {
            // Ignora chiavi vuote o strutturali (già aggiunte sopra)
            if (string.IsNullOrWhiteSpace(item.Key) || StructuralKeys.Contains(item.Key)) continue;

            var newCard = new FitsCard
            {
                Key = item.Key.ToUpper().Trim(),
                Value = item.Value,
                Comment = item.Comment,
                IsCommentStyle = (item.Key == "HISTORY" || item.Key == "COMMENT")
            };

            newHeader.AddCard(newCard);
        }

        return newHeader;
    }

    // --- UTILITIES ---

    public void TransferMetadata(FitsHeader source, FitsHeader destination)
    {
        foreach (var card in source.Cards)
        {
            if (StructuralKeys.Contains(card.Key)) continue;
            destination.AddCard(card);
        }
    }

    // --- DELEGA AI PARSER ---

    public WcsData ExtractWcs(FitsHeader header) => WcsParser.Parse(header);
    
    public GeographicLocation? GetObservatoryLocation(FitsHeader header) => GeographicParser.ParseLocation(header);

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
}