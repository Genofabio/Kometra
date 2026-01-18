using System;
using KomaLab.Models.Astrometry;
using KomaLab.Models.Fits;
using KomaLab.Models.Fits.Structure;
using KomaLab.Services.Fits.Metadata;

namespace KomaLab.Services.Fits.Parsers;

/// <summary>
/// Parser specializzato nell'estrazione della posizione geografica dagli header FITS.
/// Conosce le convenzioni delle chiavi FITS ma delega il parsing matematico a AstroParser.
/// </summary>
public static class GeographicParser
{
    // Token ordinati per "importanza" (standard prima, custom dopo)
    private static readonly string[] LatTokens = { "OBSGEO-B", "LAT-OBS", "SITELAT", "LATITUDE", "GEOLAT" };
    private static readonly string[] LonTokens = { "OBSGEO-L", "LONG-OBS", "SITELONG", "LONGITUD", "GEOLON" };
    private static readonly string[] AltTokens = { "OBSGEO-A", "ALTI-OBS", "SITEELEV", "ELEVATIO", "GEOELEV", "ELEVATION" };

    public static GeographicLocation? ParseLocation(FitsHeader header, IFitsMetadataService metadata)
    {
        // 1. Cerchiamo le stringhe grezze nell'header FITS
        string latStr = FindFirstValue(header, metadata, LatTokens);
        string lonStr = FindFirstValue(header, metadata, LonTokens);
        string altStr = FindFirstValue(header, metadata, AltTokens);

        // 2. Se mancano i dati fondamentali, usciamo
        if (string.IsNullOrEmpty(latStr) || string.IsNullOrEmpty(lonStr)) return null;

        // 3. Deleghiamo la creazione dell'oggetto al Factory Method del modello.
        // Il modello userà internamente l'AstroParser per gestire ":" o decimali.
        double? altMeters = AstroParser.ParseDegrees(altStr);
        double altKm = altMeters.HasValue ? altMeters.Value / 1000.0 : 0.5;

        return GeographicLocation.FromStrings(latStr, lonStr, altKm);
    }

    /// <summary>
    /// Restituisce la prima stringa non vuota trovata tra i token forniti.
    /// </summary>
    private static string FindFirstValue(FitsHeader header, IFitsMetadataService metadata, string[] tokens)
    {
        foreach (var token in tokens)
        {
            string val = metadata.GetStringValue(header, token);
            if (!string.IsNullOrWhiteSpace(val)) return val;
        }
        return string.Empty;
    }
}