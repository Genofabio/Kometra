using System;
using Kometra.Models.Fits;
using Kometra.Models.Astrometry;
using Kometra.Models.Fits.Structure;
using Kometra.Services.Fits.Metadata;

namespace Kometra.Services.Fits.Parsers;

/// <summary>
/// Parser specializzato nell'estrazione della posizione geografica dagli header FITS.
/// Centralizza la conoscenza delle chiavi ma delega la matematica all'AstroParser.
/// </summary>
public static class GeographicParser
{
    // Espandiamo i token per includere gli standard ASCOM, NINA e le abbreviazioni comuni
    private static readonly string[] LatTokens = { 
        "HIERARCH CAHA TEL GEOLAT", "OBSGEO-B", "LAT-OBS", "SITELAT", "LATITUDE", "OBJCTLAT", "LAT" 
    };

    private static readonly string[] LonTokens = { 
        "HIERARCH CAHA TEL GEOLON", "OBSGEO-L", "LONG-OBS", "SITELONG", "LONGITUD", "OBJCTLON", "LONG" 
    };

    private static readonly string[] AltTokens = { 
        "HIERARCH CAHA TEL GEOELEV", "OBSGEO-A", "ALTI-OBS", "SITEELEV", "ELEVATIO", "GEOELEV" 
    };

    public static GeographicLocation? ParseLocation(FitsHeader header, IFitsMetadataService metadata)
    {
        string latStr = FindFirstValue(header, metadata, LatTokens);
        string lonStr = FindFirstValue(header, metadata, LonTokens);

        // DEBUG: Vediamo cosa estraiamo dal file FITS
        System.Diagnostics.Debug.WriteLine($"[GEO DEBUG] Raw Lat: '{latStr}' | Raw Lon: '{lonStr}'");

        if (string.IsNullOrEmpty(latStr) || string.IsNullOrEmpty(lonStr)) 
        {
            System.Diagnostics.Debug.WriteLine("[GEO DEBUG] Fallimento: Una delle stringhe è vuota.");
            return null;
        }

        double? lat = AstroParser.ParseDegrees(latStr);
        double? lon = AstroParser.ParseDegrees(lonStr);

        // DEBUG: Vediamo se la matematica dell'AstroParser funziona
        System.Diagnostics.Debug.WriteLine($"[GEO DEBUG] Parsed Lat: {lat} | Parsed Lon: {lon}");

        if (lat.HasValue && lon.HasValue)
        {
            return new GeographicLocation(lat.Value, lon.Value);
        }

        System.Diagnostics.Debug.WriteLine("[GEO DEBUG] Fallimento: AstroParser ha restituito null.");
        return null;
    }

    private static string FindFirstValue(FitsHeader header, IFitsMetadataService metadata, string[] tokens)
    {
        foreach (var token in tokens)
        {
            string val = metadata.GetStringValue(header, token);
            // Se la stringa non è null, non è vuota e non contiene solo spazi
            if (!string.IsNullOrWhiteSpace(val)) 
                return val.Trim(); 
        }
        return string.Empty;
    }
}