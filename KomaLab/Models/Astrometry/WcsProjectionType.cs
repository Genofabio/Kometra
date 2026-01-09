namespace KomaLab.Models.Astrometry;

public enum WcsProjectionType
{
    Unknown,
    Tan, // Gnomonica
    Tpv, // Gnomonica + Distorsioni
    Sip  // Simple Imaging Polynomial
}