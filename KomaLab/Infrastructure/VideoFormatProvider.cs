using System;
using System.IO;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KomaLab.Models.Export;
using OpenCvSharp;
using KomaLab.Models.Visualization;

namespace KomaLab.Infrastructure;

public class VideoFormatProvider : IVideoFormatProvider
{
    private readonly Dictionary<VideoContainer, (string Ext, List<VideoCodec> Codecs)> _registry = new()
    {
        { VideoContainer.MP4, (".mp4", new() { VideoCodec.H264, VideoCodec.H265 }) },
        { VideoContainer.AVI, (".avi", new() { VideoCodec.MJPG, VideoCodec.XVID }) },
        { VideoContainer.MKV, (".mkv", new() { VideoCodec.H264, VideoCodec.XVID, VideoCodec.MJPG, VideoCodec.H265 }) }
    };

    private readonly SemaphoreSlim _semaphore = new(1, 1);
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
            // Eseguiamo la validazione in un task per non bloccare la UI
            _validatedFormats = await Task.Run(() => ValidateAvailableCodecsParallel());
            _isInitialized = true;
        }
        finally { _semaphore.Release(); }
    }

    private Dictionary<VideoContainer, List<VideoCodec>> ValidateAvailableCodecsParallel()
    {
        var result = new ConcurrentDictionary<VideoContainer, ConcurrentBag<VideoCodec>>();
        string tempDir = Path.GetTempPath();

        // 1. Lista di tutti i test da fare
        var allTests = _registry.SelectMany(kvp => 
            kvp.Value.Codecs.Select(codec => new { Container = kvp.Key, Codec = codec }))
            .ToList();

        // 2. CORREZIONE: Ordine dei backend.
        // FFMPEG deve essere il primo perché è il più compatibile (specialmente per MKV).
        // MSMF spesso fallisce su formati non nativi Windows.
        var backendsToTest = new[] { 
            VideoCaptureAPIs.FFMPEG,       // <--- PRIORITARIO
            VideoCaptureAPIs.MSMF,         // Windows Native
            VideoCaptureAPIs.AVFOUNDATION, // MacOS
            VideoCaptureAPIs.V4L2          // Linux
        };

        // 3. CORREZIONE: Limitazione del parallelismo.
        // Usare ProcessorCount (es. 12-16 thread) causa race conditions nelle DLL native di OpenCV/FFMPEG.
        // 4 thread sono il bilanciamento perfetto tra velocità e stabilità.
        var options = new ParallelOptions { MaxDegreeOfParallelism = 4 };

        Parallel.ForEach(allTests, options, testItem => 
        {
            foreach (var api in backendsToTest) 
            {
                if (TryAndCache(testItem.Container, testItem.Codec, api, tempDir)) 
                {
                    var list = result.GetOrAdd(testItem.Container, _ => new ConcurrentBag<VideoCodec>());
                    list.Add(testItem.Codec);
                    break; // Trovato un backend funzionante per questo codec, passo al prossimo codec
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
        string file = Path.Combine(dir, $"komalab_test_{Guid.NewGuid():N}{GetExtension(cont)}");
        int fourcc = GetFourCC(cod, cont);
        var size = new Size(128, 128);

        try {
            // Nota: Se openCV fallisce l'init, spesso non lancia eccezione ma IsOpened resta false
            using var writer = new VideoWriter(file, api, fourcc, 25, size, true);
            
            if (writer.IsOpened()) 
            {
                using var dummyFrame = Mat.Zeros(size, MatType.CV_8UC3);
                writer.Write(dummyFrame);
                writer.Release(); // Importante: finalizza il file

                var fileInfo = new FileInfo(file);
                // Controllo robusto: il file deve esistere ed avere contenuto
                bool isValid = fileInfo.Exists && fileInfo.Length > 500;

                if (isValid)
                {
                    _apiCache.TryAdd((cont, cod), api);
                }

                if (fileInfo.Exists) fileInfo.Delete();
                return isValid;
            }
        } 
        catch 
        {
            // Ignoriamo errori qui, significa semplicemente che il codec non è supportato
        }
        
        try { if (File.Exists(file)) File.Delete(file); } catch { }
        return false;
    }

    public VideoCaptureAPIs GetBestAPI(VideoContainer c, VideoCodec v) => 
        _apiCache.TryGetValue((c, v), out var api) ? api : VideoCaptureAPIs.ANY;

    public IEnumerable<VideoContainer> GetSupportedContainers() => _validatedFormats.Keys;
    public IEnumerable<VideoCodec> GetSupportedCodecs(VideoContainer c) => _validatedFormats.GetValueOrDefault(c) ?? new();
    public string GetExtension(VideoContainer c) => _registry[c].Ext;

    // CORREZIONE: FourCC aggiornati per massima compatibilità
    public int GetFourCC(VideoCodec codec, VideoContainer container) => (codec, container) switch
    {
        // MP4 usa standard Apple/ISO
        (VideoCodec.H264, VideoContainer.MP4) => VideoWriter.FourCC('a', 'v', 'c', '1'),
        (VideoCodec.H265, VideoContainer.MP4) => VideoWriter.FourCC('H', 'E', 'V', 'C'),
        
        // MKV preferisce codici Open Source (X264 invece di H264 è cruciale per FFMPEG)
        (VideoCodec.H264, VideoContainer.MKV) => VideoWriter.FourCC('X', '2', '6', '4'),
        (VideoCodec.H265, VideoContainer.MKV) => VideoWriter.FourCC('H', 'E', 'V', 'C'),
        
        // Codec legacy
        (VideoCodec.XVID, VideoContainer.AVI) => VideoWriter.FourCC('X', 'V', 'I', 'D'),
        (VideoCodec.XVID, VideoContainer.MKV) => VideoWriter.FourCC('X', 'V', 'I', 'D'),
        
        // Fallback MJPG
        (VideoCodec.MJPG, _) => VideoWriter.FourCC('M', 'J', 'P', 'G'),
        _ => VideoWriter.FourCC('M', 'J', 'P', 'G')
    };
}