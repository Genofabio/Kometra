using System.Collections.Generic;

namespace KomaLab.Services;

/// <summary>
/// Cache Thread-Safe a capacità fissa (Least Recently Used).
/// Quando la capacità è piena, l'elemento meno recente viene scartato.
/// </summary>
public class LruCache<TKey, TValue> where TKey : notnull
{
    private readonly int _capacity;
    private readonly Dictionary<TKey, LinkedListNode<CacheItem>> _cacheMap;
    private readonly LinkedList<CacheItem> _lruList;
    private readonly object _lock = new(); // Per thread-safety

    public LruCache(int capacity)
    {
        _capacity = capacity;
        _cacheMap = new Dictionary<TKey, LinkedListNode<CacheItem>>(capacity);
        _lruList = new LinkedList<CacheItem>();
    }

    public bool TryGet(TKey key, out TValue? value)
    {
        lock (_lock)
        {
            if (_cacheMap.TryGetValue(key, out var node))
            {
                // HIT: Sposta in cima (più recente)
                value = node.Value.Value;
                _lruList.Remove(node);
                _lruList.AddFirst(node);
                return true;
            }

            value = default;
            return false;
        }
    }

    public void Add(TKey key, TValue value)
    {
        lock (_lock)
        {
            if (_cacheMap.ContainsKey(key))
            {
                // Se esiste già, aggiornalo e promuovilo
                _lruList.Remove(_cacheMap[key]);
                _cacheMap.Remove(key);
            }
            else if (_cacheMap.Count >= _capacity)
            {
                // EVICTION: Rimuovi l'ultimo (meno recente)
                var lastNode = _lruList.Last;
                if (lastNode != null)
                {
                    _cacheMap.Remove(lastNode.Value.Key);
                    _lruList.RemoveLast();
                    // Qui il GC raccoglierà l'oggetto scartato
                }
            }

            // Aggiungi in testa (più recente)
            var newNode = new LinkedListNode<CacheItem>(new CacheItem(key, value));
            _lruList.AddFirst(newNode);
            _cacheMap.Add(key, newNode);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _lruList.Clear();
            _cacheMap.Clear();
        }
    }

    private readonly struct CacheItem
    {
        public TKey Key { get; }
        public TValue Value { get; }
        public CacheItem(TKey key, TValue value) { Key = key; Value = value; }
    }
}