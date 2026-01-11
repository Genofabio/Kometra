using System;
using System.Globalization;
using nom.tam.fits;
using KomaLab.Models.Astrometry;

namespace KomaLab.Services.Data.Parsers;

// ---------------------------------------------------------------------------
// FILE: GeographicParser.cs
// RUOLO: Parser
// DESCRIZIONE:
// Interpreta le stringhe dell'Header per estrarre informazioni geografiche
// (Latitudine, Longitudine, Altitudine). Supporta formati decimali e sessagesimali.
// Esegue una scansione a passaggio singolo dell'header per efficienza.
// ---------------------------------------------------------------------------

public static class GeographicParser
{
    private static readonly string[] LatTokens = { "SITELAT", "LATITUDE", "LAT-OBS", "GEOLAT", "GEO_LAT", "OBSGEO-B" };
    private static readonly string[] LonTokens = { "SITELONG", "LONGITUD", "LONG-OBS", "GEOLON", "GEO_LON", "OBSGEO-L" };
    private static readonly string[] AltTokens = { "ALTI-OBS", "SITEELEV", "ELEVATIO", "GEOELEV", "ELEVATION" };

    public static GeographicLocation? ParseLocation(Header header)
    {
        double? lat = null;
        double? lon = null;
        double? altMeters = null;

        var cursor = header.GetCursor();
        
        // Loop singolo ottimizzato: cerca tutte le chiavi in una passata
        while (cursor.MoveNext())
        {
            // Se abbiamo già trovato tutto, usciamo (opzionale, ma veloce)
            if (lat.HasValue && lon.HasValue && altMeters.HasValue) break;

            if (cursor.Current is not HeaderCard card) continue;

            string key = card.Key?.ToUpper() ?? "";
            
            // Parsing Lazy: parsa solo se la chiave corrisponde
            if (!lat.HasValue && ContainsToken(key, LatTokens))
            {
                lat = ParseCoordinateString(card.Value);
            }
            else if (!lon.HasValue && ContainsToken(key, LonTokens))
            {
                lon = ParseCoordinateString(card.Value);
            }
            else if (!altMeters.HasValue && ContainsToken(key, AltTokens))
            {
                altMeters = ParseCoordinateString(card.Value);
            }
        }

        if (!lat.HasValue || !lon.HasValue) return null;

        double altKm = altMeters.HasValue ? altMeters.Value / 1000.0 : 0.5; // Default 500m

        return new GeographicLocation(lat.Value, lon.Value, altKm);
    }
    
    /// <summary>
    /// Tenta di parsare una coordinata (Decimale o Sessagesimale).
    /// Gestisce la pulizia di commenti inline FITS.
    /// </summary>
    public static double? ParseCoordinateString(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        // 1. Pulizia Commenti Inline (Tutto dopo '/')
        int slashIndex = input.IndexOf('/');
        if (slashIndex > -1) input = input.Substring(0, slashIndex);

        // 2. Pulizia Caratteri
        input = input.Replace("'", "").Trim();
        if (string.IsNullOrWhiteSpace(input)) return null;

        // 3. Tentativo Decimale Veloce
        if (double.TryParse(input, NumberStyles.Any, CultureInfo.InvariantCulture, out double val)) 
            return val;

        // 4. Tentativo Sessagesimale (Lento)
        try 
        {
            string normalized = input
                .Replace(":", " ")
                .Replace("d", " ")
                .Replace("m", " ")
                .Replace("s", " ")
                .Replace("deg", " "); 
            
            string[] parts = normalized.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            
            if (parts.Length == 0) return null;

            double deg = double.Parse(parts[0], CultureInfo.InvariantCulture);
            double min = (parts.Length > 1) ? double.Parse(parts[1], CultureInfo.InvariantCulture) : 0;
            double sec = (parts.Length > 2) ? double.Parse(parts[2], CultureInfo.InvariantCulture) : 0;
            
            double res = Math.Abs(deg) + (min / 60.0) + (sec / 3600.0);
            return input.StartsWith("-") ? -res : res;
        }
        catch { return null; }
    }

    private static bool ContainsToken(string key, string[] tokens)
    {
        foreach (var token in tokens)
        {
            if (key.Contains(token)) return true;
        }
        return false;
    }
}