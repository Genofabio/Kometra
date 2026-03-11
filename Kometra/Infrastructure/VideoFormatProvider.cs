using System;
using System.IO;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using OpenCvSharp;
using Kometra.Models.Visualization;
using Kometra.Models.Export;

namespace Kometra.Infrastructure;

public class VideoFormatProvider : IVideoFormatProvider
{
    private readonly Dictionary<VideoContainer, (string Ext, List<VideoCodec> Codecs)> _registry = new()
    {
        { VideoContainer.MP4, (".mp4", new() { VideoCodec.H264, VideoCodec.H265 }) },
        { VideoContainer.AVI, (".avi", new() { VideoCodec.MJPG, VideoCodec.XVID }) },
        { VideoContainer.MKV, (".mkv", new() { VideoCodec.H264, VideoCodec.XVID, VideoCodec.MJPG, VideoCodec.H265 }) }
    };

    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly object _nativeInitLock = new(); 
    private bool _isInitialized = false;
    private Dictionary<VideoContainer, List<VideoCodec>> _validatedFormats = new();
    
    // Cache thread-safe per le API funzionanti
    private ConcurrentDictionary<(VideoContainer, VideoCodec), VideoCaptureAPIs> _apiCache = new();

    public async Task InitializeAsync()
    {
        if (_isInitialized) return;
        await _semaphore.WaitAsync();
        try {
            if (_isInitialized) return;
            
            Console.WriteLine("[VideoFormatProvider] Avvio validazione codec su " + (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "macOS" : "Windows/Linux"));
            
            // Parallelismo impostato a 1 per evitare conflitti con AVAssetWriter durante i test
            _validatedFormats = await Task.Run(() => ValidateAvailableCodecsParallel());
            
            _isInitialized = true;
            Console.WriteLine("[VideoFormatProvider] Inizializzazione completata.");
        }
        finally { _semaphore.Release(); }
    }

    private Dictionary<VideoContainer, List<VideoCodec>> ValidateAvailableCodecsParallel()
    {
        var result = new ConcurrentDictionary<VideoContainer, ConcurrentBag<VideoCodec>>();
        string tempDir = Path.GetFullPath(Path.GetTempPath());

        // 1. Lista di tutti i test da fare
        var allTests = _registry.SelectMany(kvp => 
            kvp.Value.Codecs.Select(codec => new { Container = kvp.Key, Codec = codec }))
            .ToList();

        // 2. Definizione backend in base all'OS
        var backendsToTest = new List<VideoCaptureAPIs>();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            backendsToTest.Add(VideoCaptureAPIs.AVFOUNDATION);
            backendsToTest.Add(VideoCaptureAPIs.FFMPEG);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            backendsToTest.Add(VideoCaptureAPIs.FFMPEG);
            backendsToTest.Add(VideoCaptureAPIs.MSMF);
        }
        else
        {
            backendsToTest.Add(VideoCaptureAPIs.FFMPEG);
        }

        // 3. Esecuzione test (MaxDegreeOfParallelism = 1 per stabilità su Mac)
        var options = new ParallelOptions { MaxDegreeOfParallelism = 1 };

        Parallel.ForEach(allTests, options, testItem => 
        {
            foreach (var api in backendsToTest) 
            {
                if (TryAndCache(testItem.Container, testItem.Codec, api, tempDir)) 
                {
                    var list = result.GetOrAdd(testItem.Container, _ => new ConcurrentBag<VideoCodec>());
                    list.Add(testItem.Codec);
                    break; 
                }
            }
        });

        return result.ToDictionary(
            k => k.Key,
            v => v.Value.Distinct().OrderBy(c => c).ToList()
        );
    }

    private bool TryAndCache(VideoContainer cont, VideoCodec cod, VideoCaptureAPIs api, string dir)
    {
        string fileName = $"kometra_test_{Guid.NewGuid():N}{GetExtension(cont)}";
        string filePath = Path.Combine(dir, fileName);
        int fourcc = GetFourCC(cod, cont);
        var size = new Size(128, 128);

        try {
            lock (_nativeInitLock)
            {
                using var writer = new VideoWriter();
                // Apertura esplicita con backend selezionato
                bool opened = writer.Open(filePath, api, fourcc, 25, size, true);
                
                if (opened && writer.IsOpened()) 
                {
                    using var dummyFrame = Mat.Zeros(size, MatType.CV_8UC3);
                    writer.Write(dummyFrame);
                    writer.Release(); 

                    var fileInfo = new FileInfo(filePath);
                    bool isValid = fileInfo.Exists && fileInfo.Length > 500;

                    if (isValid)
                    {
                        Console.WriteLine($"[Test OK] {cont}/{cod} tramite {api}. Dimensione: {fileInfo.Length} bytes");
                        _apiCache.TryAdd((cont, cod), api);
                        if (fileInfo.Exists) fileInfo.Delete();
                        return true;
                    }
                }
                else
                {
                    Console.WriteLine($"[Test FAIL] {cont}/{cod} tramite {api}. IsOpened: false");
                }
            }
        } 
        catch (Exception ex)
        {
            Console.WriteLine($"[Test ERROR] {cont}/{cod} tramite {api}. Errore: {ex.Message}");
        }
        
        if (File.Exists(filePath)) try { File.Delete(filePath); } catch { }
        return false;
    }

    public VideoCaptureAPIs GetBestAPI(VideoContainer c, VideoCodec v) => 
        _apiCache.TryGetValue((c, v), out var api) ? api : VideoCaptureAPIs.ANY;

    public IEnumerable<VideoContainer> GetSupportedContainers() => _validatedFormats.Keys;
    public IEnumerable<VideoCodec> GetSupportedCodecs(VideoContainer c) => _validatedFormats.GetValueOrDefault(c) ?? new();
    public string GetExtension(VideoContainer c) => _registry[c].Ext;

    public int GetFourCC(VideoCodec codec, VideoContainer container)
    {
        bool isMac = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

        return (codec, container) switch
        {
            // MP4: H264 e H265 confermati dai log su Mac
            (VideoCodec.H264, VideoContainer.MP4) => VideoWriter.FourCC('a', 'v', 'c', '1'),
            (VideoCodec.H265, VideoContainer.MP4) => isMac ? VideoWriter.FourCC('h', 'v', 'c', '1') : VideoWriter.FourCC('H', 'E', 'V', 'C'),
            
            // MKV/AVI: Fallback per compatibilità cross-platform
            (VideoCodec.H264, VideoContainer.MKV) => VideoWriter.FourCC('X', '2', '6', '4'),
            (VideoCodec.H265, VideoContainer.MKV) => VideoWriter.FourCC('H', 'E', 'V', 'C'),
            (VideoCodec.XVID, _) => VideoWriter.FourCC('X', 'V', 'I', 'D'),
            (VideoCodec.MJPG, _) => VideoWriter.FourCC('M', 'J', 'P', 'G'),
            _ => VideoWriter.FourCC('M', 'J', 'P', 'G')
        };
    }
}