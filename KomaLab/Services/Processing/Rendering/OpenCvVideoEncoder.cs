using System;
using System.IO;
using OpenCvSharp;

namespace KomaLab.Services.Processing.Rendering;

public class OpenCvVideoEncoder : IVideoEncoder
{
    private VideoWriter? _writer;

    public void Initialize(string path, double fps, int width, int height, int fourCc, VideoCaptureAPIs api)
    {
        // Pulizia preventiva
        Dispose();

        _writer = new VideoWriter();
        
        // Tentativo con l'API suggerita dal provider
        _writer.Open(path, api, fourCc, fps, new Size(width, height), true);

        // Se l'API specifica fallisce (raro dopo la validazione), tentiamo il fallback automatico
        if (!_writer.IsOpened())
        {
            _writer.Open(path, VideoCaptureAPIs.ANY, fourCc, fps, new Size(width, height), true);
        }

        if (!_writer.IsOpened())
        {
            throw new IOException($"Impossibile inizializzare il VideoWriter per il percorso: {path}. " +
                                  "Il codec o il formato selezionato non sono supportati dal sistema.");
        }
    }

    public void WriteFrame(Mat frame)
    {
        if (_writer == null || !_writer.IsOpened())
            throw new InvalidOperationException("L'encoder non è stato inizializzato correttamente o è stato chiuso.");

        _writer.Write(frame);
    }

    public void Dispose()
    {
        if (_writer != null)
        {
            _writer.Release(); // Cruciale per scrivere l'header finale (evita file da 258 byte)
            _writer.Dispose();
            _writer = null;
        }
    }
}