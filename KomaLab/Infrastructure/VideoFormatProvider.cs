using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;
using KomaLab.Models.Visualization;

namespace KomaLab.Infrastructure;

public class VideoFormatProvider : IVideoFormatProvider
{
    private readonly Dictionary<VideoContainer, (string Ext, List<VideoCodec> Codecs)> _registry = new()
    {
        { VideoContainer.MP4, (".mp4", new() { VideoCodec.H264 }) },
        { VideoContainer.AVI, (".avi", new() { VideoCodec.MJPG, VideoCodec.XVID }) },
        { VideoContainer.MKV, (".mkv", new() { VideoCodec.H264, VideoCodec.XVID, VideoCodec.MJPG }) }
    };

    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private bool _isInitialized = false;
    private Dictionary<VideoContainer, List<VideoCodec>> _validatedFormats = new();
    private Dictionary<(VideoContainer, VideoCodec), VideoCaptureAPIs> _apiCache = new();

    public async Task InitializeAsync()
    {
        if (_isInitialized) return;
        await _semaphore.WaitAsync();
        try {
            if (_isInitialized) return;
            // Validazione asincrona per evitare blocchi UI e gestire thread-model diversi (MTA)
            _validatedFormats = await Task.Run(() => ValidateAvailableCodecs());
            _isInitialized = true;
        }
        finally { _semaphore.Release(); }
    }

    private Dictionary<VideoContainer, List<VideoCodec>> ValidateAvailableCodecs()
    {
        var result = new Dictionary<VideoContainer, List<VideoCodec>>();
        string tempDir = Path.GetTempPath();

        // Backend da testare in ordine di preferenza
        var backendsToTest = new[] { 
            VideoCaptureAPIs.FFMPEG, 
            VideoCaptureAPIs.MSMF,   // Windows
            VideoCaptureAPIs.AVFOUNDATION, // macOS
            VideoCaptureAPIs.V4L2    // Linux
        };

        foreach (var container in _registry) {
            var workingCodecs = new List<VideoCodec>();
            foreach (var codec in container.Value.Codecs) {
                
                bool foundWorkingBackend = false;
                foreach (var api in backendsToTest) {
                    if (TryAndCache(container.Key, codec, api, tempDir)) {
                        workingCodecs.Add(codec);
                        foundWorkingBackend = true;
                        break; 
                    }
                }
            }
            if (workingCodecs.Any()) result.Add(container.Key, workingCodecs);
        }
        return result;
    }

    private bool TryAndCache(VideoContainer cont, VideoCodec cod, VideoCaptureAPIs api, string dir)
    {
        string file = Path.Combine(dir, $"komalab_test_{Guid.NewGuid():N}{GetExtension(cont)}");
        int fourcc = GetFourCC(cod, cont);
        
        try {
            // Test con dimensione standard (molti encoder falliscono con 1x1 o 16x16)
            using var writer = new VideoWriter(file, api, fourcc, 25, new Size(128, 128), true);
            if (writer.IsOpened()) {
                writer.Release();
                if (File.Exists(file)) File.Delete(file);
                _apiCache[(cont, cod)] = api;
                return true;
            }
        } catch { /* Backend non supportato o errore codec */ }
        return false;
    }

    public VideoCaptureAPIs GetBestAPI(VideoContainer c, VideoCodec v) => 
        _apiCache.TryGetValue((c, v), out var api) ? api : VideoCaptureAPIs.ANY;

    public IEnumerable<VideoContainer> GetSupportedContainers() => _validatedFormats.Keys;
    public IEnumerable<VideoCodec> GetSupportedCodecs(VideoContainer c) => _validatedFormats.GetValueOrDefault(c) ?? new();
    public string GetExtension(VideoContainer c) => _registry[c].Ext;

    public int GetFourCC(VideoCodec codec, VideoContainer container) => (codec, container) switch
    {
        // Tag universali per massima compatibilità cross-platform
        (VideoCodec.H264, VideoContainer.MP4) => VideoWriter.FourCC('a','v','c','1'),
        (VideoCodec.H264, VideoContainer.MKV) => VideoWriter.FourCC('H','2','6','4'),
        (VideoCodec.MJPG, _) => VideoWriter.FourCC('M','J','P','G'),
        (VideoCodec.XVID, _) => VideoWriter.FourCC('X','V','I','D'),
        _ => VideoWriter.FourCC('M','J','P','G')
    };
}