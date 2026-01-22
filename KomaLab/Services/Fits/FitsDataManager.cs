using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices; // Necessario per Marshal.SizeOf
using System.Threading.Tasks;
using KomaLab.Models.Fits;
using KomaLab.Models.Fits.Structure;
using KomaLab.Services.Fits.IO;
using Microsoft.Extensions.Caching.Memory;

namespace KomaLab.Services.Fits;

public class FitsDataManager : IFitsDataManager
{
    private readonly IFitsIoService _ioService;
    private readonly IMemoryCache _cache;
    
    private readonly ConcurrentBag<string> _tempFilesTracker = new();
    private static readonly TimeSpan CacheExpiration = TimeSpan.FromMinutes(10);

    public FitsDataManager(IFitsIoService ioService, IMemoryCache cache)
    {
        _ioService = ioService ?? throw new ArgumentNullException(nameof(ioService));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    // =======================================================================
    // 1. LETTURA (Logica di Caching)
    // =======================================================================

    public async Task<FitsDataPackage> GetDataAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Path vuoto");

        return await _cache.GetOrCreateAsync(path, async entry =>
        {
            entry.SlidingExpiration = CacheExpiration;

            var header = await _ioService.ReadHeaderAsync(path);
            var pixels = await _ioService.ReadPixelDataAsync(path);

            if (header == null || pixels == null)
                throw new FileNotFoundException($"File non valido: {path}");

            // MODIFICA: Calcolo deterministico della dimensione per il limite globale
            entry.Size = CalculateArraySize(pixels); 
            
            return new FitsDataPackage(path, header, pixels);
        }) ?? throw new InvalidOperationException("Errore cache");
    }

    public async Task<FitsHeader?> GetHeaderOnlyAsync(string path)
    {
        if (_cache.TryGetValue(path, out FitsDataPackage? package) && package != null)
            return package.Header;

        return await _ioService.ReadHeaderAsync(path);
    }

    // =======================================================================
    // 2. SCRITTURA E GENERAZIONE TEMPORANEI
    // =======================================================================

    public async Task SaveDataAsync(string path, Array pixels, FitsHeader header)
    {
        await _ioService.WriteFileAsync(path, pixels, header);

        var package = new FitsDataPackage(path, header, pixels);
        _cache.Set(path, package, new MemoryCacheEntryOptions 
        { 
            SlidingExpiration = CacheExpiration,
            // MODIFICA: Calcolo deterministico della dimensione per il limite globale
            Size = CalculateArraySize(pixels)
        });
    }

    public async Task<FitsFileReference> SaveAsTemporaryAsync(Array pixels, FitsHeader header, string context)
    {
        string fullPath = _ioService.BuildRawPath(context, $"{Guid.NewGuid()}.fits");

        await SaveDataAsync(fullPath, pixels, header);
        _tempFilesTracker.Add(fullPath);

        return new FitsFileReference(fullPath);
    }

    // =======================================================================
    // 3. GESTIONE SANDBOX (Copia Disco-Disco)
    // =======================================================================

    public async Task<string> CreateSandboxCopyAsync(string originalPath, string context)
    {
        string tempPath = _ioService.BuildRawPath(context, $"sandbox_{Guid.NewGuid()}.fits");
        await _ioService.CopyFileAsync(originalPath, tempPath);
        _tempFilesTracker.Add(tempPath);
        
        return tempPath;
    }

    // =======================================================================
    // 4. MANUTENZIONE E CLEANUP
    // =======================================================================

    public void Invalidate(string path) => _cache.Remove(path);

    public void DeleteTemporaryData(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        Invalidate(path);
        _ioService.TryDeleteFile(path);
    }

    public void ClearCache()
    {
        if (_cache is MemoryCache concreteCache)
        {
            concreteCache.Compact(1.0);
        }
    }

    public void Clear()
    {
        ClearCache();
        foreach (var tempPath in _tempFilesTracker)
        {
            _ioService.TryDeleteFile(tempPath);
        }
        while (_tempFilesTracker.TryTake(out _)) { }
    }

    // =======================================================================
    // HELPERS DI CALCOLO DIMENSIONE
    // =======================================================================

    /// <summary>
    /// Calcola l'occupazione in byte dell'array di pixel per permettere 
    /// alla MemoryCache di rispettare il SizeLimit globale configurato.
    /// </summary>
    private long CalculateArraySize(Array array)
    {
        if (array == null) return 0;
        // Numero totale di elementi * dimensione in byte del tipo (short, int, double, etc.)
        return (long)array.Length * Marshal.SizeOf(array.GetType().GetElementType()!);
    }
}