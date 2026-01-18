using System;
using System.Collections.Generic;
using KomaLab.Models.Fits.Structure;
using KomaLab.Services.Fits.Metadata;

namespace KomaLab.ViewModels.Fits;

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
            if (string.IsNullOrWhiteSpace(row.Key)) continue;

            string keyUpper = row.Key.Trim().ToUpper();
        
            // Se è END, lo aggiungiamo così com'è (o lo saltiamo se lo gestisce il Writer, 
            // ma per l'editor in memoria meglio averlo).
            if (keyUpper == "END")
            {
                newHeader.AddCard(new FitsCard("END", "", "", true));
                continue;
            }

            // FORMATTAZIONE INTELLIGENTE DEL VALORE
            string rawValue = row.Value;
            string formattedValue = FormatValueForFits(keyUpper, rawValue);

            newHeader.AddCard(new FitsCard(
                Key: keyUpper, 
                Value: formattedValue, 
                Comment: row.Comment, 
                IsCommentStyle: false));
        }
        return newHeader;
    }
    
    private string FormatValueForFits(string key, string uiValue)
    {
        if (string.IsNullOrWhiteSpace(uiValue)) return "";

        // Se l'utente ha già messo gli apici, li teniamo
        if (uiValue.StartsWith("'") && uiValue.EndsWith("'")) return uiValue;

        // Tentiamo di capire il tipo
        // Numeri e Booleani (T/F) vanno scritti senza apici
        if (double.TryParse(uiValue, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out _))
            return uiValue; // È un numero

        if (uiValue == "T" || uiValue == "F") 
            return uiValue; // È un booleano logico

        // Se siamo qui, è una stringa libera -> AGGIUNGIAMO APICI
        // Es. L'utente scrive: M31 -> FITS vuole: 'M31'
        return $"'{uiValue.Replace("'", "''")}'";
    }
}