using System;
using System.Collections.Concurrent;
using System.IO;
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
    
    // Tracciamento centralizzato per la pulizia a fine sessione
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

            entry.Size = Buffer.ByteLength(pixels); 
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

        // Aggiornamento immediato cache (Write-Through)
        var package = new FitsDataPackage(path, header, pixels);
        _cache.Set(path, package, new MemoryCacheEntryOptions 
        { 
            SlidingExpiration = CacheExpiration,
            Size = Buffer.ByteLength(pixels)
        });
    }

    public async Task<FitsFileReference> SaveAsTemporaryAsync(Array pixels, FitsHeader header, string context)
    {
        // Chiediamo all'IO service un path grezzo ma lo gestiamo noi
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
        // Generazione path tramite meccanismo di IO Service
        string tempPath = _ioService.BuildRawPath(context, $"sandbox_{Guid.NewGuid()}.fits");
        
        // Esecuzione copia tecnica
        await _ioService.CopyFileAsync(originalPath, tempPath);
        
        // Registrazione per il cleanup
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
        // Svuotiamo solo la memoria gestita da Microsoft.Extensions.Caching.Memory
        // Il parametro 1.0 (100%) forza la rimozione di tutti gli elementi, 
        // partendo da quelli non prioritari.
        if (_cache is MemoryCache concreteCache)
        {
            concreteCache.Compact(1.0);
        }
    
        // NOTA: Non tocchiamo _tempFilesTracker. 
        // I file su disco rimangono dove sono, pronti per essere letti di nuovo se serve.
    }

    public void Clear()
    {
        // Prima svuotiamo la RAM
        ClearCache();

        // Poi puliamo fisicamente il disco (Solo ora!)
        foreach (var tempPath in _tempFilesTracker)
        {
            _ioService.TryDeleteFile(tempPath);
        }
    
        while (_tempFilesTracker.TryTake(out _)) { }
    }
}