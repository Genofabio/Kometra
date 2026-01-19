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
        
        if (card == null) return string.Empty;

        if (AdditiveKeys.Contains(key)) return card.Comment ?? card.Value ?? string.Empty;

        if (string.IsNullOrWhiteSpace(card.Value)) return string.Empty;

        string val = card.Value.Trim();
        if (val.StartsWith("'") && val.EndsWith("'"))
        {
            val = val.Substring(1, val.Length - 2);
            val = val.Replace("''", "'");
        }
        return val.Trim();
    }

    // --- INTERPRETAZIONE SCIENTIFICA ---

    public DateTime? GetObservationDate(FitsHeader header)
    {
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
        int? bitpix = GetIntValue(header, "BITPIX");

        return (bitpix ?? 16) switch
        {
            8 => FitsBitDepth.UInt8,
            16 => FitsBitDepth.Int16,
            32 => FitsBitDepth.Int32,
            -32 => FitsBitDepth.Float,
            -64 => FitsBitDepth.Double,
            _ => FitsBitDepth.Int16 
        };
    }
    
    public SkyCoordinate? GetTargetCoordinates(FitsHeader header)
    {
        if (header == null) return null;

        // 1. Identificazione delle Card (RA e DEC con fallback)
        var raCard = header.Cards.FirstOrDefault(c => c.Key.Equals("RA", StringComparison.OrdinalIgnoreCase))
                     ?? header.Cards.FirstOrDefault(c => c.Key.Equals("OBJCTRA", StringComparison.OrdinalIgnoreCase));

        var decCard = header.Cards.FirstOrDefault(c => c.Key.Equals("DEC", StringComparison.OrdinalIgnoreCase))
                      ?? header.Cards.FirstOrDefault(c => c.Key.Equals("OBJCTDEC", StringComparison.OrdinalIgnoreCase));

        if (raCard == null || decCard == null) return null;

        double raDeg = 0;
        double decDeg = 0;

        // --- 2. LOGICA SMART PER RA (Ascensione Retta) ---
        string raValRaw = GetStringValue(header, raCard.Key);
        string raComment = raCard.Comment?.ToLowerInvariant() ?? "";

        if (double.TryParse(raValRaw, NumberStyles.Any, CultureInfo.InvariantCulture, out double rawRa))
        {
            // Se il commento dice "deg" o se il valore è palesemente > 24, sono gradi
            if (raComment.Contains("deg") || rawRa > 24)
            {
                raDeg = rawRa;
            }
            else
            {
                // Altrimenti assumiamo ore (standard FITS per RA < 24) e convertiamo in gradi
                raDeg = rawRa * 15.0;
            }
        }
        else
        {
            // Se è una stringa sessagesimale (es. "01:07:07"), il parser la tratta come ore
            raDeg = AstroParser.ParseHoursToDegrees(raValRaw) ?? 0;
        }

        // --- 3. LOGICA PER DEC (Declinazione) ---
        string decValRaw = GetStringValue(header, decCard.Key);
    
        // Il parser delle Declinazioni gestisce già sia gradi decimali che sessagesimali
        decDeg = AstroParser.ParseDegrees(decValRaw) ?? 0;

        return new SkyCoordinate(raDeg, decDeg);
    }
    
    // --- SCRITTURA / MANIPOLAZIONE ---

    public void AddValue(FitsHeader header, string key, object value, string? comment = null)
    {
        header.AddCard(CreateCard(key, value, comment));
    }

    public void SetValue(FitsHeader header, string key, object value, string? comment = null)
    {
        if (!AdditiveKeys.Contains(key)) header.RemoveCard(key);
        AddValue(header, key, value, comment);
    }

    public void TransferMetadata(FitsHeader source, FitsHeader destination)
    {
        if (source == null || destination == null) return;

        // Pulizia preventiva dell'END per garantire il riposizionamento finale
        RemoveEndKey(destination);

        foreach (var card in source.Cards)
        {
            if (StructuralKeys.Contains(card.Key)) continue;

            // Saltiamo l'END della sorgente per evitare duplicati mal posizionati
            if (card.Key.Equals("END", StringComparison.OrdinalIgnoreCase)) continue;

            destination.AddCard(card); 
        }

        // Sigillo finale
        EnsureEndKey(destination);
    }

    public FitsHeader CreateHeaderFromTemplate(FitsHeader template, Array newPixels, FitsBitDepth depth)
    {
        var newHeader = new FitsHeader();
        
        AddValue(newHeader, "SIMPLE", true, "Standard FITS format");
        AddValue(newHeader, "BITPIX", (int)depth, GetBitpixComment((int)depth));
        AddValue(newHeader, "NAXIS", 2, "2D Image");
        AddValue(newHeader, "NAXIS1", newPixels.GetLength(1), "Image Width");
        AddValue(newHeader, "NAXIS2", newPixels.GetLength(0), "Image Height");
        AddValue(newHeader, "BSCALE", 1.0);
        AddValue(newHeader, "BZERO", 0.0);
        
        if (template != null) TransferMetadata(template, newHeader);
        
        // Garantisce la chiusura dell'header anche in assenza di template
        EnsureEndKey(newHeader);
        
        return newHeader;
    }

    // --- UTILITY ---

    public IEnumerable<T> SortByDate<T>(IEnumerable<T> items, Func<T, FitsHeader?> headerSelector)
    {
        if (items == null) return Enumerable.Empty<T>();
        return items.OrderBy(item => GetObservationDate(headerSelector(item)!) ?? DateTime.MinValue);
    }

    // --- HELPER INTERNI ---

    private void RemoveEndKey(FitsHeader header)
    {
        header.RemoveCard("END");
    }

    private void EnsureEndKey(FitsHeader header)
    {
        // Se non esiste già un marcatore END, lo aggiungiamo in coda.
        // Se esistesse già (ma rimosso da RemoveEndKey), verrà aggiunto qui alla fine.
        if (!header.Cards.Any(c => c.Key.Equals("END", StringComparison.OrdinalIgnoreCase)))
        {
            header.AddCard(new FitsCard("END", string.Empty, string.Empty, false));
        }
    }

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
        if (AdditiveKeys.Contains(key)) return s; 
        if (string.IsNullOrEmpty(s)) return "''";

        string escaped = s.Replace("'", "''");
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