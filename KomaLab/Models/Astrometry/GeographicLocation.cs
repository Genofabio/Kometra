namespace KomaLab.Models.Astrometry;

// ---------------------------------------------------------------------------
// FILE: GeographicLocation.cs
// DESCRIZIONE:
// Definisce la posizione dell'osservatorio sulla superficie terrestre.
// Fondamentale per calcoli astrometrici che richiedono correzioni topocentriche.
// ---------------------------------------------------------------------------

/// <summary>
/// Rappresenta le coordinate geografiche immutabili dell'osservatorio.
/// </summary>
/// <param name="Latitude">Latitudine in gradi decimali (Positivo = Nord, Negativo = Sud). Range: -90 a +90.</param>
/// <param name="Longitude">Longitudine in gradi decimali (Positivo = Est, Negativo = Ovest). Range: -180 a +180.</param>
/// <param name="AltitudeKm">Altitudine sul livello del mare in Km. Default: 0.5 km.</param>
public record GeographicLocation(double Latitude, double Longitude, double AltitudeKm = 0.5)
{
    public double AltitudeMeters => AltitudeKm * 1000.0;
}