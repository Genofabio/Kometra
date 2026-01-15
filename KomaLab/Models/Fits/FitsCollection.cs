using System;
using System.Collections.Generic;
using System.Linq;
using KomaLab.Services.Utilities; 

namespace KomaLab.Models.Fits;

public class FitsCollection
{
    private readonly List<FitsFileReference> _files = new();
    
    // La cache vive qui perché è legata al ciclo di vita di questo gruppo di immagini
    public LruCache<string, Array> PixelCache { get; } 

    public IReadOnlyList<FitsFileReference> Files => _files;
    public int Count => _files.Count;
    public FitsFileReference this[int index] => _files[index];

    public FitsCollection(IEnumerable<string> paths, int cacheSize = 3)
    {
        PixelCache = new LruCache<string, Array>(cacheSize);
        foreach (var p in paths) _files.Add(new FitsFileReference(p));
    }
    
    // Helper per trovare riferimenti (logica in-memory, ok nel model)
    public FitsFileReference? FindByPath(string path) 
        => _files.FirstOrDefault(x => x.FilePath == path);
}