using System;
using KomaLab.Models.Visualization;
using OpenCvSharp;

namespace KomaLab.Services.Processing.Rendering;

/// <summary>
/// Interfaccia di basso livello per la scrittura di flussi video.
/// Si occupa esclusivamente dell'interazione con il backend multimediale.
/// </summary>
public interface IVideoEncoder : IDisposable
{
    /// <summary>
    /// Inizializza il file video e il codec.
    /// </summary>
    /// <param name="path">Percorso assoluto del file di output.</param>
    /// <param name="fps">Frame per secondo.</param>
    /// <param name="width">Larghezza in pixel.</param>
    /// <param name="height">Altezza in pixel.</param>
    /// <param name="fourCc">Codice identificativo del codec (es. 'M','J','P','G').</param>
    /// <param name="isColor">Indica se il flusso video deve supportare il colore.</param>
    public void Initialize(string path, double fps, int width, int height, int fourCc, VideoCaptureAPIs api);

    /// <summary>
    /// Scrive un singolo frame nel file video.
    /// </summary>
    /// <param name="frame">Matrice OpenCV contenente i dati del frame.</param>
    void WriteFrame(Mat frame);
}