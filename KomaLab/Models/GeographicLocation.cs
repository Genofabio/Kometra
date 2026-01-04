namespace KomaLab.Models;

/// <summary>
/// Rappresenta le coordinate geografiche dell'osservatorio sulla Terra.
/// Necessario per il calcolo della parallasse (coordinate topocentriche).
/// </summary>
/// <param name="Latitude">Latitudine in gradi decimali (Positivo = Nord).</param>
/// <param name="Longitude">Longitudine in gradi decimali (Positivo = Est).</param>
/// <param name="AltitudeKm">Altitudine in Km (default 0.5km se sconosciuta).</param>
public record GeographicLocation(double Latitude, double Longitude, double AltitudeKm = 0.5);