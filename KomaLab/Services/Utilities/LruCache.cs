using System;
using System.Collections.Generic;

namespace KomaLab.Services.Utilities;

// ---------------------------------------------------------------------------
// FILE: LruCache.cs
// RUOLO: Gestore Memoria Temporanea (Utility)
// DESCRIZIONE:
// Cache Thread-Safe con politica Least Recently Used (LRU).
// Mantiene in memoria solo gli elementi più utilizzati fino alla capacità massima.
// 
// OTTIMIZZAZIONE ENTERPRISE:
// Implementa il supporto a IDisposable. Se un elemento viene scartato dalla 
// cache e implementa IDisposable (es. Mat di OpenCV o buffer FITS), viene 
// disposato immediatamente per prevenire memory leak unmanaged.
// ---------------------------------------------------------------------------

public class LruCache<TKey, TValue> where TKey : notnull
{
    private readonly int _capacity;
    private readonly Dictionary<TKey, LinkedListNode<CacheItem>> _cacheMap;
    private readonly LinkedList<CacheItem> _lruList;
    private readonly object _lock = new();

    public LruCache(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity), "La capacità deve essere positiva.");
        
        _capacity = capacity;
        _cacheMap = new Dictionary<TKey, LinkedListNode<CacheItem>>(capacity);
        _lruList = new LinkedList<CacheItem>();
    }

    /// <summary>
    /// Tenta di recuperare un valore dalla cache. 
    /// Se presente, l'elemento viene promosso come "più recente".
    /// </summary>
    public bool TryGet(TKey key, out TValue? value)
    {
        lock (_lock)
        {
            if (_cacheMap.TryGetValue(key, out var node))
            {
                // HIT: Sposta in cima alla lista (Recentissimo)
                _lruList.Remove(node);
                _lruList.AddFirst(node);
                value = node.Value.Value;
                return true;
            }

            value = default;
            return false;
        }
    }

    /// <summary>
    /// Aggiunge o aggiorna un valore. 
    /// Se la capacità è superata, scarta l'elemento meno usato.
    /// </summary>
    public void Add(TKey key, TValue value)
    {
        lock (_lock)
        {
            if (_cacheMap.TryGetValue(key, out var existingNode))
            {
                // Se esiste già, aggiorniamo il valore e puliamo il vecchio se necessario
                DisposeItem(existingNode.Value.Value);
                _lruList.Remove(existingNode);
                _cacheMap.Remove(key);
            }
            else if (_cacheMap.Count >= _capacity)
            {
                // EVICTION: Raggiunta capacità massima
                var lastNode = _lruList.Last;
                if (lastNode != null)
                {
                    _cacheMap.Remove(lastNode.Value.Key);
                    _lruList.RemoveLast();
                    
                    // CRITICO: Libera la memoria dell'elemento scartato
                    DisposeItem(lastNode.Value.Value);
                }
            }

            // Inserimento in testa
            var newNode = new LinkedListNode<CacheItem>(new CacheItem(key, value));
            _lruList.AddFirst(newNode);
            _cacheMap.Add(key, newNode);
        }
    }

    /// <summary>
    /// Rimuove esplicitamente un elemento dalla cache.
    /// </summary>
    public void Remove(TKey key)
    {
        lock (_lock)
        {
            if (_cacheMap.TryGetValue(key, out var node))
            {
                DisposeItem(node.Value.Value);
                _lruList.Remove(node);
                _cacheMap.Remove(key);
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            foreach (var node in _cacheMap.Values)
            {
                DisposeItem(node.Value.Value);
            }
            _lruList.Clear();
            _cacheMap.Clear();
        }
    }

    /// <summary>
    /// Helper per la gestione sicura della memoria unmanaged/pesante.
    /// </summary>
    private static void DisposeItem(TValue? item)
    {
        if (item is IDisposable disposable)
        {
            try { disposable.Dispose(); } catch { /* Ignore */ }
        }
    }

    private readonly struct CacheItem
    {
        public TKey Key { get; }
        public TValue Value { get; }
        public CacheItem(TKey key, TValue value)
        {
            Key = key;
            Value = value;
        }
    }
}