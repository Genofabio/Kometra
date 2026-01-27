namespace KomaLab.Models.Visualization;

/// <summary>
/// Impacchetta tutte le scelte dell'utente per l'esportazione video.
/// </summary>
/// <param name="InitialProfile">Contiene le soglie di contrasto (Black/White ADU) regolate nella Viewport.</param>
public record VideoExportSettings(
    string OutputPath,
    double Fps,
    VideoContainer Container,
    VideoCodec Codec,
    AbsoluteContrastProfile InitialProfile, // <--- Fondamentale per applicare lo stretching della Viewport
    double ScaleFactor = 1.0,
    VisualizationMode Mode = VisualizationMode.Linear,
    bool AdaptiveStretch = true
);

/// <summary>
/// Formati contenitore supportati dal sistema di export.
/// </summary>
public enum VideoContainer
{
    MP4,
    AVI,
    MKV
}

/// <summary>
/// Algoritmi di compressione validati dal VideoFormatProvider.
/// </summary>
public enum VideoCodec
{
    /// <summary> Motion JPEG: Altissima compatibilità, file pesanti. </summary>
    MJPG, 
    
    /// <summary> XVID MPEG-4: Ottimo bilanciamento qualità/peso. </summary>
    XVID, 
    
    /// <summary> H.264: Standard moderno ad alta efficienza. </summary>
    H264,
}