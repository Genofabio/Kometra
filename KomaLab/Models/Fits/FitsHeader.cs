using System;
using System.Collections.Generic;
using System.Linq;

namespace KomaLab.Models.Fits;

public class FitsHeader
{
    private readonly List<FitsCard> _cards = new();
    public IReadOnlyList<FitsCard> Cards => _cards;

    public void AddCard(FitsCard card) => _cards.Add(card);

    public void RemoveCard(string key)
    {
        _cards.RemoveAll(c => c.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Crea una copia profonda dell'header.
    /// Essendo FitsCard un record, non serve un metodo Clone() manuale per ogni card.
    /// </summary>
    public FitsHeader Clone()
    {
        var newHeader = new FitsHeader();
        // I record in C# sono immutabili per definizione (se usi le proprietà posizionali).
        // Quindi basta aggiungere il riferimento alla card: è intrinsecamente sicuro.
        foreach (var card in _cards)
            newHeader.AddCard(card); 
            
        return newHeader;
    }
}