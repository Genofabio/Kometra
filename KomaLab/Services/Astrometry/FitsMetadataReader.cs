using System;
using System.Globalization;
using nom.tam.fits;
using System.Diagnostics;
using KomaLab.Models;
using KomaLab.Models.Astrometry;

namespace KomaLab.Services.Astrometry;

public static class FitsMetadataReader
{
    // Token di ricerca (parti uniche delle chiavi)
    private static readonly string[] LatTokens = { "SITELAT", "LATITUDE", "LAT-OBS", "GEOLAT", "GEO_LAT", "OBSGEO-B" };
    private static readonly string[] LonTokens = { "SITELONG", "LONGITUD", "LONG-OBS", "GEOLON", "GEO_LON", "OBSGEO-L" };
    private static readonly string[] AltTokens = { "ALTI-OBS", "SITEELEV", "ELEVATIO", "GEOELEV", "ELEVATION" };

    public static GeographicLocation? ReadObservatoryLocation(Header header)
    {
        if (header == null) return null;

        // 1. Cerchiamo Latitudine e Longitudine
        double? lat = FindFuzzyValue(header, LatTokens);
        double? lon = FindFuzzyValue(header, LonTokens);

        // Se manca una delle due coordinate, il luogo non è valido
        if (!lat.HasValue || !lon.HasValue) 
        {
            return null;
        }

        // 2. Cerchiamo l'altitudine (facoltativo, default 0.5 km)
        double altKm = 0.5;
        double? altMeters = FindFuzzyValue(header, AltTokens);
        if (altMeters.HasValue) altKm = altMeters.Value / 1000.0;

        return new GeographicLocation(lat.Value, lon.Value, altKm);
    }

    /// <summary>
    /// Scansiona l'header cercando una chiave che contenga uno dei token specificati.
    /// Include la logica di riparazione per chiavi HIERARCH malformate o con spazi.
    /// </summary>
    public static double? FindFuzzyValue(Header header, string[] searchTokens)
    {
        var cursor = header.GetCursor();
        while (cursor.MoveNext())
        {
            string key = "";
            string value = "";
            string rawCard = "";

            // A. Estrazione Dati Base
            if (cursor.Current is HeaderCard hc) 
            { 
                key = hc.Key; 
                value = hc.Value; 
                rawCard = hc.ToString(); // Cruciale per il parsing manuale
            }
            else if (cursor.Current is System.Collections.DictionaryEntry de && de.Value is HeaderCard hcd) 
            { 
                key = hcd.Key; 
                value = hcd.Value;
                rawCard = hcd.ToString();
            }

            if (string.IsNullOrEmpty(key)) continue;

            string keyUpper = key.Trim().ToUpper();

            // B. Esclusione Commenti/History (per non leggere dati vecchi nel testo libero)
            if (keyUpper == "COMMENT" || keyUpper == "HISTORY" || keyUpper == "END") continue;

            // C. FIX: HIERARCH "Sporchi"
            // Se la chiave è solo "HIERARCH" (o contiene punti), dobbiamo parsare la riga grezza
            // per trovare la VERA chiave (es. "CAHA TEL GEOLAT") e il VERO valore.
            if (keyUpper.StartsWith("HIERARCH"))
            {
                // Parsing manuale della riga FITS: "KEY = VALUE / COMMENT"
                int eqIndex = rawCard.IndexOf('=');
                if (eqIndex > 0)
                {
                    // 1. Estraiamo la chiave reale
                    string realKey = rawCard.Substring(0, eqIndex).Trim().ToUpper();
                    
                    // Puliamo eventuale prefisso "HIERARCH" e normalizziamo i punti in spazi
                    // Es: "HIERARCH.CAHA.TEL" -> "CAHA TEL"
                    realKey = realKey.Replace("HIERARCH", "").Replace(".", " ").Trim();
                    
                    // Sovrascriviamo la chiave da controllare
                    keyUpper = realKey;

                    // 2. Estraiamo il valore reale (tutto ciò che è tra '=' e '/')
                    string valPart = rawCard.Substring(eqIndex + 1);
                    int slashIndex = valPart.IndexOf('/');
                    
                    if (slashIndex >= 0)
                    {
                        // Prendiamo solo la parte prima del commento!
                        value = valPart.Substring(0, slashIndex).Trim();
                    }
                    else
                    {
                        value = valPart.Trim();
                    }
                    
                    // Puliamo apici dalle stringhe (es. '37.22')
                    value = value.Replace("'", "");
                }
            }

            // D. Matching dei Token
            foreach (var token in searchTokens)
            {
                // Controlliamo se la chiave (normalizzata) contiene il token (es. "GEOLAT")
                if (keyUpper.Contains(token))
                {
                    // Proviamo a parsare il valore trovato
                    double? result = ParseCoordinateString(value);
                    if (result.HasValue) 
                    {
                        // Debug.WriteLine($"[FitsReader] Trovato {token} in '{keyUpper}' = {result}");
                        return result;
                    }
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Parsa una stringa di coordinate (es. "12:34:56", "12 34 56", "12.543").
    /// </summary>
    public static double? ParseCoordinateString(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        
        // Pulizia preliminare
        input = input.Replace("'", "").Trim();

        // Tentativo 1: Numero decimale semplice (es. "37.2236")
        if (double.TryParse(input, NumberStyles.Any, CultureInfo.InvariantCulture, out double val)) 
            return val;

        // Tentativo 2: Formato sessagesimale (HH:MM:SS o DD MM SS)
        try 
        {
            // Normalizza i separatori
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
            
            bool isNegative = input.Trim().StartsWith("-");
            double decimalDeg = Math.Abs(deg) + (min / 60.0) + (sec / 3600.0);
            
            return isNegative ? -decimalDeg : decimalDeg;
        }
        catch 
        { 
            return null; 
        }
    }
}