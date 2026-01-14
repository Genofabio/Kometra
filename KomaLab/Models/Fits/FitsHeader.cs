using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace KomaLab.Models.Fits;

public class FitsHeader
{
    private readonly List<FitsCard> _cards = new();

    public IReadOnlyList<FitsCard> Cards => _cards;

    public void AddCard(FitsCard card) => _cards.Add(card);

    public void Add(string key, object value, string? comment = null)
    {
        string valStr = value switch
        {
            bool b => b ? "T" : "F",
            double d => d.ToString("G", CultureInfo.InvariantCulture),
            int i => i.ToString(),
            _ => value.ToString() ?? ""
        };

        _cards.Add(new FitsCard 
        { 
            Key = key.ToUpper(), 
            Value = valStr, 
            Comment = comment 
        });
    }

    // --- HELPERS DI LETTURA ---

    /// <summary>
    /// Metodo generico per convertire il valore.
    /// </summary>
    public T? GetValue<T>(string key, T? defaultValue = default) where T : struct
    {
        var card = _cards.FirstOrDefault(c => c.Key == key);
        if (card == null || string.IsNullOrWhiteSpace(card.Value)) return defaultValue;

        try
        {
            return (T)Convert.ChangeType(card.Value, typeof(T), CultureInfo.InvariantCulture);
        }
        catch
        {
            return defaultValue;
        }
    }

    /// <summary>
    /// Helper specifico per interi (usato dal Reader per NAXIS, BITPIX).
    /// </summary>
    public int GetIntValue(string key, int defaultValue = 0)
    {
        var card = _cards.FirstOrDefault(c => c.Key == key);
        if (card == null || string.IsNullOrWhiteSpace(card.Value)) return defaultValue;

        // Parsing robusto (Any permette spazi e segni)
        if (int.TryParse(card.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out int result))
        {
            return result;
        }
        return defaultValue;
    }

    public string GetStringValue(string key)
    {
        var card = _cards.FirstOrDefault(c => c.Key == key);
        return card?.Value?.Replace("'", "").Trim() ?? string.Empty;
    }
    
    /// <summary>
    /// Crea una copia completa dell'header e di tutte le sue card.
    /// Modificare il clone non influenzerà l'originale.
    /// </summary>
    public FitsHeader Clone()
    {
        var newHeader = new FitsHeader();
        foreach (var card in _cards)
        {
            // Fondamentale: chiamiamo Clone() sulla card, non passiamo il riferimento!
            newHeader.AddCard(card.Clone());
        }
        return newHeader;
    }
}