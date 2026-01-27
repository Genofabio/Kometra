namespace KomaLab.Models.Visualization;

public record VideoExportSettings(
    string OutputPath,
    double Fps,
    VideoContainer Container, // Aggiunto
    VideoCodec Codec,         // Aggiunto
    double ScaleFactor = 1.0,
    VisualizationMode Mode = VisualizationMode.Linear,
    bool AdaptiveStretch = true
);

public enum VideoContainer
{
    MP4,
    AVI,
    MKV
}

public enum VideoCodec
{
    // FourCC: Motion JPEG (Molto compatibile, poco compresso)
    MJPG, 
    // FourCC: XVID MPEG-4 (Buon compromesso)
    XVID, 
    // FourCC: H264 (Standard moderno, richiede librerie esterne)
    H264,
}