using System;
using System.Collections.Generic;
using KomaLab.Models.Fits;
using KomaLab.Services.Fits;
using KomaLab.Services.Fits.Metadata;
using KomaLab.ViewModels.Items;

namespace KomaLab.ViewModels.Mappings;

public class FitsHeaderUiMapper
{
    private readonly IFitsMetadataService _metadataService;

    public FitsHeaderUiMapper(IFitsMetadataService metadataService)
    {
        _metadataService = metadataService ?? throw new ArgumentNullException(nameof(metadataService));
    }

    /// <summary>
    /// Trasforma l'header FITS in una lista di righe per l'editor.
    /// Applica le regole di sola lettura definite dal servizio.
    /// </summary>
    public List<FitsHeaderEditorRow> MapToRows(FitsHeader header)
    {
        if (header == null) return new List<FitsHeaderEditorRow>();

        var list = new List<FitsHeaderEditorRow>();
        foreach (var card in header.Cards)
        {
            // Chiede al servizio se questa chiave è strutturale (ReadOnly)
            bool isReadOnly = _metadataService.IsStructuralKey(card.Key);

            // Crea la riga UI (nota: il costruttore setta IsModified = false)
            var row = new FitsHeaderEditorRow(card.Key, card.Value, card.Comment, isReadOnly);
            list.Add(row);
        }
        return list;
    }

    /// <summary>
    /// Ricostruisce un FitsHeader valido partendo dalle righe dell'editor.
    /// </summary>
    public FitsHeader ReconstructHeader(IEnumerable<FitsHeaderEditorRow> rows)
    {
        var newHeader = new FitsHeader();
        if (rows == null) return newHeader;

        foreach (var row in rows)
        {
            if (!string.IsNullOrWhiteSpace(row.Key))
            {
                // Qui assumiamo che FitsCard sia il record/classe base
                newHeader.AddCard(new FitsCard(
                    Key: row.Key.ToUpper(), 
                    Value: row.Value, 
                    Comment: row.Comment, 
                    IsCommentStyle: false));
            }
        }
        return newHeader;
    }
}