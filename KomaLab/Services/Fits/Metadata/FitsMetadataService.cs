using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using KomaLab.Models.Astrometry;
using KomaLab.Models.Astrometry.Wcs;
using KomaLab.Models.Fits;
using KomaLab.Models.Fits.Structure;
using KomaLab.Services.Fits.Parsers;

namespace KomaLab.Services.Fits.Metadata;

public class FitsMetadataService : IFitsMetadataService
{
    // Chiavi che definiscono la struttura fisica del file e non devono essere toccate manualmente
    private static readonly HashSet<string> StructuralKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "SIMPLE", "BITPIX", "NAXIS", "NAXIS1", "NAXIS2", "NAXIS3", 
        "EXTEND", "PCOUNT", "GCOUNT", "GROUPS", "BSCALE", "BZERO"
    };

    // Chiavi che ammettono duplicati (Commenti, Storia)
    private static readonly HashSet<string> AdditiveKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "HISTORY", "COMMENT", "" 
    };

    // --- REGOLE DI DOMINIO ---

    public bool IsStructuralKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        return StructuralKeys.Contains(key);
    }

    public bool AreGeometricallyCompatible(FitsHeader h1, FitsHeader h2)
    {
        if (h1 == null || h2 == null) return false;
        return GetIntValue(h1, "NAXIS1") == GetIntValue(h2, "NAXIS1") &&
               GetIntValue(h1, "NAXIS2") == GetIntValue(h2, "NAXIS2");
    }

    // --- LETTURA VALORI BASE ---

    public int GetIntValue(FitsHeader header, string key, int defaultValue = 0)
    {
        var val = GetStringValue(header, key);
        return int.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out int res) ? res : defaultValue;
    }

    public double GetDoubleValue(FitsHeader header, string key, double defaultValue = 0.0)
    {
        var val = GetStringValue(header, key);
        return double.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out double res) ? res : defaultValue;
    }

    public string GetStringValue(FitsHeader header, string key)
    {
        if (header == null) return string.Empty;
        var card = header.Cards.FirstOrDefault(c => c.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        
        // Se la card non esiste
        if (card == null) return string.Empty;

        // COMMENT e HISTORY ritornano il contenuto grezzo (spesso messo nel campo comment o value senza apici)
        if (AdditiveKeys.Contains(key)) return card.Comment ?? card.Value ?? string.Empty;

        if (string.IsNullOrWhiteSpace(card.Value)) return string.Empty;

        // Gestione standard FITS per le stringhe: Rimuovi apici esterni e gestisci escape '' -> '
        string val = card.Value.Trim();
        if (val.StartsWith("'") && val.EndsWith("'"))
        {
            // Rimuovi apici esterni
            val = val.Substring(1, val.Length - 2);
            // Un-escape dei doppi apici
            val = val.Replace("''", "'");
        }
        return val.Trim();
    }

    // --- INTERPRETAZIONE SCIENTIFICA (Parser Delegati) ---

    public DateTime? GetObservationDate(FitsHeader header)
    {
        // Logica specifica: DATE-OBS è standard, DATE è fallback
        string dateStr = GetStringValue(header, "DATE-OBS");
        if (string.IsNullOrEmpty(dateStr)) dateStr = GetStringValue(header, "DATE");
        
        if (DateTime.TryParse(dateStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)) return dt;
        return null;
    }

    public double? GetFocalLength(FitsHeader header) => OpticalParser.ParseFocalLength(header, this);
    public double? GetPixelSize(FitsHeader header) => OpticalParser.ParsePixelSize(header, this);
    public WcsData ExtractWcs(FitsHeader header) => WcsParser.Parse(header, this);
    public GeographicLocation? GetObservatoryLocation(FitsHeader header) => GeographicParser.ParseLocation(header, this);

    public FitsBitDepth GetBitDepth(FitsHeader header)
    {
        // BITPIX è una keyword OBBLIGATORIA nello standard FITS.
        // I valori possibili sono: 8, 16, 32, -32 (float), -64 (double)
        int? bitpix = GetIntValue(header, "BITPIX");

        // Se bitpix è null, usiamo 16 come fallback (il più comune in astronomia amatoriale)
        return (bitpix ?? 16) switch
        {
            8 => FitsBitDepth.UInt8,
            16 => FitsBitDepth.Int16,
            32 => FitsBitDepth.Int32,
            -32 => FitsBitDepth.Float,
            -64 => FitsBitDepth.Double,
            _ => FitsBitDepth.Int16 // Fallback per valori non standard
        };
    }
    
    // --- SCRITTURA / MANIPOLAZIONE ---

    public void AddValue(FitsHeader header, string key, object value, string? comment = null)
    {
        header.AddCard(CreateCard(key, value, comment));
    }

    public void SetValue(FitsHeader header, string key, object value, string? comment = null)
    {
        // Se non è additiva (HISTORY/COMMENT), rimuovi le precedenti occorrenze per sovrascrivere
        if (!AdditiveKeys.Contains(key)) header.RemoveCard(key);
        AddValue(header, key, value, comment);
    }

    public void TransferMetadata(FitsHeader source, FitsHeader destination)
    {
        if (source == null || destination == null) return;
        foreach (var card in source.Cards)
        {
            // 1. Salta le chiavi strutturali (NAXIS, BITPIX...)
            if (StructuralKeys.Contains(card.Key)) continue;

            // 2. Salta ESPLICITAMENTE "END". 
            // Vogliamo vederla nell'editor, ma non vogliamo copiarla nel nuovo header
            // perché il Writer ne genererà una nuova alla fine fisica del file.
            if (card.Key.Equals("END", StringComparison.OrdinalIgnoreCase)) continue;

            destination.AddCard(card); 
        }
    }

    public FitsHeader CreateHeaderFromTemplate(FitsHeader template, Array newPixels, FitsBitDepth depth)
    {
        var newHeader = new FitsHeader();
        // Definizione struttura base obbligatoria
        AddValue(newHeader, "SIMPLE", true, "Standard FITS format");
        AddValue(newHeader, "BITPIX", (int)depth, GetBitpixComment((int)depth));
        AddValue(newHeader, "NAXIS", 2, "2D Image");
        AddValue(newHeader, "NAXIS1", newPixels.GetLength(1), "Image Width");
        AddValue(newHeader, "NAXIS2", newPixels.GetLength(0), "Image Height");
        AddValue(newHeader, "BSCALE", 1.0);
        AddValue(newHeader, "BZERO", 0.0);
        
        // Copia il resto dei metadati (Observer, Object, Telescope...)
        if (template != null) TransferMetadata(template, newHeader);
        
        return newHeader;
    }

    // --- UTILITY ---

    public IEnumerable<T> SortByDate<T>(IEnumerable<T> items, Func<T, FitsHeader?> headerSelector)
    {
        if (items == null) return Enumerable.Empty<T>();
        return items.OrderBy(item => GetObservationDate(headerSelector(item)!) ?? DateTime.MinValue);
    }

    // --- HELPER INTERNI ---

    private FitsCard CreateCard(string key, object value, string? comment)
    {
        string keyUpper = key.ToUpper();
        string valStr = value switch
        {
            bool b => b ? "T" : "F",
            double d => d.ToString("G", CultureInfo.InvariantCulture),
            int i => i.ToString(),
            string s => FormatFitsString(s, keyUpper),
            _ => value?.ToString() ?? ""
        };

        return new FitsCard(keyUpper, valStr, comment, false);
    }

    private string FormatFitsString(string s, string key)
    {
        if (AdditiveKeys.Contains(key)) return s; // COMMENT non ha apici
        if (string.IsNullOrEmpty(s)) return "''";

        // FITS Standard: Escape single quote with two single quotes
        string escaped = s.Replace("'", "''");
        
        // Padding a 8 caratteri per leggibilità (opzionale ma consigliato)
        return $"'{escaped,-8}'"; 
    }

    private string GetBitpixComment(int bitpix) => bitpix switch
    {
        8 => "8-bit Unsigned Integer",
        16 => "16-bit Integer",
        32 => "32-bit Integer",
        -32 => "Single Precision Float",
        -64 => "Double Precision Float",
        _ => "Unknown"
    };
}