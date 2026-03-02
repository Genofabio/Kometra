using KomaLab.Models.Visualization;

namespace KomaLab.Models.Export;

public enum ExportFormat
{
    Fits,
    PNG,
    JPEG
}

public enum FitsCompressionMode
{
    None,
    Rice,
    Gzip // Aggiunto per supportare il codec Gzip implementato
}

/// <summary>
/// Contiene tutti i parametri necessari per eseguire il job di esportazione.
/// </summary>
public class ExportJobSettings
{
    public string OutputDirectory { get; }
    
    /// <summary>
    /// Nome base per i file. Se vuoto, verrà usato il nome originale.
    /// Nel caso di Merge, definisce il nome del file unico.
    /// </summary>
    public string BaseFileName { get; }
    
    public ExportFormat Format { get; }
    
    // --- Opzioni FITS ---
    public bool MergeIntoSingleFile { get; }
    
    /// <summary>
    /// Modalità di compressione Tile da applicare ai file FITS.
    /// </summary>
    public FitsCompressionMode Compression { get; } // Rinominato per coerenza con i servizi IO

    // --- Opzioni Immagini (JPG/PNG) ---
    public int JpegQuality { get; } // 10-100
    
    // --- Opzioni Stretch ---
    
    /// <summary>
    /// Il profilo di contrasto da applicare.
    /// Può essere un SigmaProfile (adattivo) o un AbsoluteContrastProfile (fisso).
    /// </summary>
    public ContrastProfile Profile { get; }

    public ExportJobSettings(
        string outputDirectory, 
        string baseFileName, 
        ExportFormat format, 
        bool mergeIntoSingleFile, 
        FitsCompressionMode compression, // Coerente con la proprietà
        int jpegQuality,
        ContrastProfile profile)
    {
        OutputDirectory = outputDirectory;
        BaseFileName = baseFileName;
        Format = format;
        MergeIntoSingleFile = mergeIntoSingleFile;
        Compression = compression;
        JpegQuality = jpegQuality;
        Profile = profile;
    }
}