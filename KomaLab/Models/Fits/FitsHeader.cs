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

    /// <summary>
    /// Verifica se una chiave esiste nell'header (case-insensitive).
    /// </summary>
    public bool ContainsKey(string key)
    {
        return _cards.Any(c => c.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Rimuove tutte le occorrenze di una chiave.
    /// </summary>
    public void RemoveCard(string key)
    {
        _cards.RemoveAll(c => c.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
    }

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

    public T? GetValue<T>(string key, T? defaultValue = default) where T : struct
    {
        var card = _cards.FirstOrDefault(c => c.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
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

    public int GetIntValue(string key, int defaultValue = 0)
    {
        var card = _cards.FirstOrDefault(c => c.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (card == null || string.IsNullOrWhiteSpace(card.Value)) return defaultValue;

        if (int.TryParse(card.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out int result))
        {
            return result;
        }
        return defaultValue;
    }

    public string GetStringValue(string key)
    {
        var card = _cards.FirstOrDefault(c => c.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        return card?.Value?.Replace("'", "").Trim() ?? string.Empty;
    }
    
    public FitsHeader Clone()
    {
        var newHeader = new FitsHeader();
        foreach (var card in _cards)
        {
            newHeader.AddCard(card.Clone());
        }
        return newHeader;
    }
}