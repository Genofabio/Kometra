using System;
using System.Globalization;
using nom.tam.fits;
using System.Diagnostics;
using KomaLab.Models; // Assicurati che GeographicLocation sia qui

namespace KomaLab.Services.Astrometry;

public static class FitsMetadataReader
{
    // Definiamo i token da cercare (non le chiavi esatte, ma parti uniche)
    private static readonly string[] LatTokens = { "SITELAT", "LATITUDE", "LAT-OBS", "GEOLAT", "GEO_LAT", "OBSGEO-B" };
    private static readonly string[] LonTokens = { "SITELONG", "LONGITUD", "LONG-OBS", "GEOLON", "GEO_LON", "OBSGEO-L" };

    public static GeographicLocation? ReadObservatoryLocation(Header header)
    {
        // Usiamo la ricerca "Smart" che gestisce HIERARCH e chiavi strane
        double? lat = FindFuzzyValue(header, LatTokens);
        double? lon = FindFuzzyValue(header, LonTokens);

        if (!lat.HasValue || !lon.HasValue) 
        {
            return null;
        }

        // Cerchiamo l'altitudine (opzionale)
        double altKm = 0.5;
        double? altMeters = FindFuzzyValue(header, new[] { "ALTI-OBS", "SITEELEV", "ELEVATIO", "GEOELEV", "ELEVATION" });
        
        if (altMeters.HasValue) altKm = altMeters.Value / 1000.0;

        return new GeographicLocation(lat.Value, lon.Value, altKm);
    }

    /// <summary>
    /// Cerca nell'header un valore numerico associato a una chiave che CONTIENE uno dei token.
    /// Risolve il problema delle chiavi "HIERARCH CAHA TEL GEOLAT".
    /// </summary>
    public static double? FindFuzzyValue(Header header, string[] searchTokens)
    {
        var cursor = header.GetCursor();
        while (cursor.MoveNext())
        {
            // Otteniamo la chiave e il valore in modo sicuro
            string key = "";
            string value = "";

            if (cursor.Current is HeaderCard hc) 
            { 
                key = hc.Key; value = hc.Value; 
            }
            else if (cursor.Current is System.Collections.DictionaryEntry de && de.Value is HeaderCard hcd) 
            { 
                key = hcd.Key; value = hcd.Value; 
            }

            if (string.IsNullOrEmpty(key)) continue;

            // Normalizziamo la chiave
            string keyUpper = key.ToUpper();

            // Controlliamo se la chiave contiene uno dei nostri token (es. "HIERARCH...GEOLAT" contiene "GEOLAT")
            foreach (var token in searchTokens)
            {
                if (keyUpper.Contains(token))
                {
                    // Proviamo a parsare il valore
                    double? result = ParseCoordinateString(value);
                    if (result.HasValue) return result;
                }
            }
        }
        return null;
    }

    // --- Metodi di parsing esistenti (invariati) ---
    public static double? ParseCoordinateString(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        input = input.Replace("'", "").Trim();

        if (double.TryParse(input, NumberStyles.Any, CultureInfo.InvariantCulture, out double val)) return val;

        try 
        {
            string normalized = input.Replace(":", " ").Replace("d", " ").Replace("m", " ").Replace("s", " ").Replace("deg", " ");
            string[] parts = normalized.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return null;

            double deg = double.Parse(parts[0], CultureInfo.InvariantCulture);
            double min = (parts.Length > 1) ? double.Parse(parts[1], CultureInfo.InvariantCulture) : 0;
            double sec = (parts.Length > 2) ? double.Parse(parts[2], CultureInfo.InvariantCulture) : 0;
            bool isNegative = input.Trim().StartsWith("-");
            double decimalDeg = Math.Abs(deg) + (min / 60.0) + (sec / 3600.0);
            return isNegative ? -decimalDeg : decimalDeg;
        }
        catch { return null; }
    }
}