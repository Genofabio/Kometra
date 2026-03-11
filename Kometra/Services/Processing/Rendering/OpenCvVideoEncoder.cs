using System;
using System.IO;
using OpenCvSharp;

namespace Kometra.Services.Processing.Rendering;

public class OpenCvVideoEncoder : IVideoEncoder
{
    private VideoWriter? _writer;

    public void Initialize(string path, double fps, int width, int height, int fourCc, VideoCaptureAPIs api)
    {
        // 1. Pulizia preventiva dello stato interno
        Dispose();

        // 2. Gestione Directory e File preesistenti
        // IMPORTANTE: AVAssetWriter su Mac fallisce se il file esiste già.
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(path))
        {
            try { File.Delete(path); } catch { }
        }

        _writer = new VideoWriter();
        
        // 3. Tentativo di apertura con l'API specifica (es. AVFOUNDATION su Mac)
        // Passiamo esplicitamente 'true' per isColor poiché i frame vengono convertiti in BGR nel coordinator
        bool opened = _writer.Open(path, api, fourCc, fps, new Size(width, height), true);

        // 4. Fallback automatico se l'API suggerita fallisce
        if (!opened || !_writer.IsOpened())
        {
            // Se il primo tentativo ha creato un file corrotto/vuoto, lo rimuoviamo prima del fallback
            if (File.Exists(path)) try { File.Delete(path); } catch { }
            
            _writer.Open(path, VideoCaptureAPIs.ANY, fourCc, fps, new Size(width, height), true);
        }

        // 5. Verifica finale
        if (!_writer.IsOpened())
        {
            // Recuperiamo informazioni per un errore parlante
            string errorInfo = $"Path: {path}, API: {api}, FourCC: {fourCc}, Res: {width}x{height}";
            
            throw new IOException($"Impossibile inizializzare il VideoWriter di OpenCV. " +
                                  $"Il backend video non supporta la combinazione richiesta. Details: [{errorInfo}]");
        }
    }

    public void WriteFrame(Mat frame)
    {
        if (_writer == null || !_writer.IsOpened())
        {
            throw new InvalidOperationException("L'encoder non è stato inizializzato correttamente o è già stato chiuso.");
        }

        if (frame.Empty()) return;

        _writer.Write(frame);
    }

    public void Dispose()
    {
        if (_writer != null)
        {
            // Release() è l'operazione più critica: su Mac chiude il file e scrive 
            // gli atomi finali del contenitore (moov atom), rendendo il video leggibile.
            if (_writer.IsOpened())
            {
                _writer.Release();
            }
            
            _writer.Dispose();
            _writer = null;
        }
    }
}