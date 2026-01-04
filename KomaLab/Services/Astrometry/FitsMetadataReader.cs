using System;
using System.Globalization;
using nom.tam.fits;
using System.Diagnostics;
using KomaLab.Models;

namespace KomaLab.Services.Astrometry;

public static class FitsMetadataReader
{
    private static readonly string[] LatKeys = { "SITELAT", "LATITUDE", "LAT-OBS", "TELESCOP_LAT", "OBSGEO-B", "GEOLAT", "GEO_LAT" };
    private static readonly string[] LonKeys = { "SITELONG", "LONGITUD", "LONG-OBS", "TELESCOP_LON", "SITELON", "OBSGEO-L", "GEOLON", "GEO_LON" };

    public static GeographicLocation? ReadObservatoryLocation(Header header)
    {
        double? lat = ScanHeaderByIndex(header, LatKeys);
        double? lon = ScanHeaderByIndex(header, LonKeys);

        if (!lat.HasValue || !lon.HasValue) 
        {
            Debug.WriteLine("[FitsMetadata] ❌ GPS non trovato (scan completo).");
            return null;
        }

        double altKm = 0.5;
        double? altMeters = GetDoubleOrNull(header, "ALTI-OBS") ?? 
                            GetDoubleOrNull(header, "SITEELEV") ?? 
                            GetDoubleOrNull(header, "ELEVATIO") ??
                            ScanHeaderByIndex(header, new[] { "GEOELEV", "ELEVATION" });

        if (altMeters.HasValue) altKm = altMeters.Value / 1000.0;

        return new GeographicLocation(lat.Value, lon.Value, altKm);
    }

    private static double? ScanHeaderByIndex(Header header, string[] searchTokens)
    {
        // Iterazione sicura per indice
        int cardCount = header.NumberOfCards;
        
        for (int i = 0; i < cardCount; i++)
        {
            var card = header.GetCard(i);
            if (card == null) continue;

            string rawCard = card.ToString().ToUpper();

            foreach (var token in searchTokens)
            {
                if (rawCard.Contains(token))
                {
                    return ExtractNumberFromRawCard(rawCard);
                }
            }
        }
        return null;
    }

    private static double? ExtractNumberFromRawCard(string rawCard)
    {
        int eqIndex = rawCard.IndexOf('=');
        if (eqIndex == -1) return null;

        string valuePart = rawCard.Substring(eqIndex + 1);
        int slashIndex = valuePart.IndexOf('/');
        if (slashIndex > -1) valuePart = valuePart.Substring(0, slashIndex);

        return ParseCoordinateString(valuePart.Trim());
    }

    private static double? GetDoubleOrNull(Header header, string key)
    {
        string val = header.GetStringValue(key);
        return ParseCoordinateString(val);
    }

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