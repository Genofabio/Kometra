namespace KomaLab.Models.Astrometry;

/// <summary>
/// Rappresenta le coordinate geografiche immutabili dell'osservatorio.
/// </summary>
public record GeographicLocation(double Latitude, double Longitude, double AltitudeKm = 0.5)
{
    public double AltitudeMeters => AltitudeKm * 1000.0;

    /// <summary>
    /// Factory Method per creare una posizione a partire da stringhe (decimali o sessagesimali).
    /// </summary>
    public static GeographicLocation? FromStrings(string latStr, string lonStr, double altKm = 0.5)
    {
        double? lat = AstroParser.ParseDegrees(latStr);
        double? lon = AstroParser.ParseDegrees(lonStr);

        if (lat.HasValue && lon.HasValue)
        {
            return new GeographicLocation(lat.Value, lon.Value, altKm);
        }

        return null;
    }
}