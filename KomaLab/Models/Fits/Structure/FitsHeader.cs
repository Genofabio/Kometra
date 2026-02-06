using System;
using System.Collections.Generic;
using System.Linq;

namespace KomaLab.Models.Fits.Structure;

public class FitsHeader
{
    private readonly List<FitsCard> _cards = new();
    public IReadOnlyList<FitsCard> Cards => _cards;

    public void AddCard(FitsCard card) => _cards.Add(card);

    public void RemoveCard(string key)
    {
        _cards.RemoveAll(c => c.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
    }

    // --- NUOVO METODO AGGIUNTO ---
    public void AddOrUpdateCard(string key, string value, string? comment = null)
    {
        var existing = _cards.FirstOrDefault(c => c.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            int index = _cards.IndexOf(existing);
            // Sostituiamo la card mantenendo il commento originale se quello nuovo è null
            _cards[index] = new FitsCard(key, value, comment ?? existing.Comment, false);
        }
        else
        {
            AddCard(new FitsCard(key, value, comment ?? string.Empty, false));
        }
    }

    public FitsHeader Clone()
    {
        var newHeader = new FitsHeader();
        foreach (var card in _cards)
            newHeader.AddCard(card); 
        return newHeader;
    }
}