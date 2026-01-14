using System;
using System.Globalization;
using KomaLab.Models.Astrometry;
using KomaLab.Models.Fits;

namespace KomaLab.Services.Fits.Parsers;

// ---------------------------------------------------------------------------
// FILE: GeographicParser.cs
// RUOLO: Parser
// ---------------------------------------------------------------------------

public static class GeographicParser
{
    private static readonly string[] LatTokens = { "SITELAT", "LATITUDE", "LAT-OBS", "GEOLAT", "OBSGEO-B" };
    private static readonly string[] LonTokens = { "SITELONG", "LONGITUD", "LONG-OBS", "GEOLON", "OBSGEO-L" };
    private static readonly string[] AltTokens = { "ALTI-OBS", "SITEELEV", "ELEVATIO", "GEOELEV", "ELEVATION" };

    public static GeographicLocation? ParseLocation(FitsHeader header)
    {
        double? lat = null;
        double? lon = null;
        double? altMeters = null;

        foreach (var card in header.Cards)
        {
            if (lat.HasValue && lon.HasValue && altMeters.HasValue) break;

            string key = card.Key;
            string val = card.Value; 

            if (string.IsNullOrWhiteSpace(val)) continue;

            if (!lat.HasValue && ContainsToken(key, LatTokens))
                lat = ParseCoordinateString(val); // <--- Chiamata aggiornata
            
            else if (!lon.HasValue && ContainsToken(key, LonTokens))
                lon = ParseCoordinateString(val); // <--- Chiamata aggiornata
            
            else if (!altMeters.HasValue && ContainsToken(key, AltTokens))
                altMeters = ParseCoordinateString(val); // <--- Chiamata aggiornata
        }

        if (!lat.HasValue || !lon.HasValue) return null;

        double altKm = altMeters.HasValue ? altMeters.Value / 1000.0 : 0.5;
        return new GeographicLocation(lat.Value, lon.Value, altKm);
    }

    // MODIFICA: Reso PUBLIC e rinominato ParseCoordinateString per essere usato da JPLService
    public static double? ParseCoordinateString(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        input = input.Replace("'", "").Trim();

        // 1. Tentativo Decimale
        if (double.TryParse(input, NumberStyles.Any, CultureInfo.InvariantCulture, out double val)) 
            return val;

        // 2. Tentativo Sessagesimale
        try 
        {
            char[] separators = { ':', 'd', 'm', 's', '°', '\'', '"', ' ' };
            string[] parts = input.Split(separators, StringSplitOptions.RemoveEmptyEntries);
            
            if (parts.Length == 0) return null;

            double d = double.Parse(parts[0], CultureInfo.InvariantCulture);
            double m = parts.Length > 1 ? double.Parse(parts[1], CultureInfo.InvariantCulture) : 0;
            double s = parts.Length > 2 ? double.Parse(parts[2], CultureInfo.InvariantCulture) : 0;
            
            double res = Math.Abs(d) + (m / 60.0) + (s / 3600.0);
            return input.StartsWith("-") ? -res : res;
        }
        catch 
        { 
            return null; 
        }
    }

    private static bool ContainsToken(string key, string[] tokens)
    {
        foreach (var t in tokens) if (key.Contains(t)) return true;
        return false;
    }
}