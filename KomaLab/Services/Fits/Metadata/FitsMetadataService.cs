using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using KomaLab.Models.Astrometry;
using KomaLab.Models.Astrometry.Wcs;
using KomaLab.Models.Fits.Structure;
using KomaLab.Services.Fits.Parsers;

namespace KomaLab.Services.Fits.Metadata;

public class FitsMetadataService : IFitsMetadataService
{
    // --- COSTANTI ---

    private static readonly HashSet<string> StructuralKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "SIMPLE", "BITPIX", "NAXIS", "NAXIS1", "NAXIS2", "NAXIS3", 
        "EXTEND", "PCOUNT", "GCOUNT", "GROUPS", "BSCALE", "BZERO", "END",
        "CHECKSUM", "DATASUM", "DATE", "CREATOR", "ORIGIN"
    };

    private static readonly HashSet<string> AdditiveKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "HISTORY", "COMMENT", "" 
    };

    // --- REGOLE DI DOMINIO ---

    public bool IsStructuralKey(string key) => 
        !string.IsNullOrWhiteSpace(key) && StructuralKeys.Contains(key);

    public bool AreGeometricallyCompatible(FitsHeader h1, FitsHeader h2)
    {
        if (h1 == null || h2 == null) return false;
        return GetIntValue(h1, "NAXIS1") == GetIntValue(h2, "NAXIS1") &&
               GetIntValue(h1, "NAXIS2") == GetIntValue(h2, "NAXIS2");
    }

    // --- LETTURA GENERICA (Invariata) ---

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
            val = val.Substring(1, val.Length - 2).Replace("''", "'");
        }
        return val.Trim();
    }

    // =======================================================================
    // NUOVA IMPLEMENTAZIONE: GESTIONE TEMPORALE SCIENTIFICA
    // =======================================================================

    public DateTime? GetObservationDate(FitsHeader header)
    {
        // Lettura ISO-8601 (Precisione millisecondo richiesta per NEO/Comete) [cite: 170]
        string dateStr = GetStringValue(header, "DATE-OBS");
        if (string.IsNullOrEmpty(dateStr)) dateStr = GetStringValue(header, "DATE");
        
        // Uso di Round-trip ("O") o generico per supportare formati come "2025-01-26T14:30:00.123"
        if (DateTime.TryParse(dateStr, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt)) 
            return dt;
            
        return null;
    }

    /// <summary>
    /// Restituisce la durata dell'esposizione in secondi.
    /// Necessaria per calcolare il punto medio dell'osservazione.
    /// </summary>
    public double GetExposureDuration(FitsHeader header)
    {
        // Standard FITS 
        double exp = GetDoubleValue(header, "EXPTIME");
        // Fallback comune
        if (exp <= 0) exp = GetDoubleValue(header, "EXPOSURE");
        return exp;
    }

    /// <summary>
    /// Calcola il punto medio temporale (Time Mid-Point) dell'esposizione.
    /// CRUCIALE per l'astrometria di oggetti veloci (Comete/NEO).
    /// Formula: DATE-OBS + (EXPTIME / 2) 
    /// </summary>
    public DateTime? GetObservationMidPoint(FitsHeader header)
    {
        var start = GetObservationDate(header);
        if (start == null) return null;

        double duration = GetExposureDuration(header);
        // Se l'esposizione è 0 (es. Bias frame), il midpoint coincide con lo start
        return start.Value.AddSeconds(duration / 2.0);
    }

    /// <summary>
    /// Restituisce il Modified Julian Date (MJD).
    /// Preferibile per calcoli orbitali numerici in quanto continuo.
    /// Se manca la chiave MJD-OBS, la calcola da DATE-OBS.
    /// 
    /// </summary>
    public double? GetModifiedJulianDate(FitsHeader header)
    {
        // 1. Tentativo diretto (Standard)
        double mjd = GetDoubleValue(header, "MJD-OBS", -1.0);
        if (mjd > 0) return mjd;

        // 2. Fallback: Calcolo da DATE-OBS
        // Epoch MJD: 17 Nov 1858 00:00:00 UTC
        var dateObs = GetObservationDate(header);
        if (dateObs.HasValue)
        {
            var mjdEpoch = new DateTime(1858, 11, 17, 0, 0, 0, DateTimeKind.Utc);
            // Assicuriamoci che la data sia UTC
            var utcDate = dateObs.Value.Kind == DateTimeKind.Unspecified 
                ? DateTime.SpecifyKind(dateObs.Value, DateTimeKind.Utc) 
                : dateObs.Value.ToUniversalTime();

            return (utcDate - mjdEpoch).TotalDays;
        }

        return null;
    }

    /// <summary>
    /// Rileva se il telescopio stava inseguendo un oggetto non siderale (Cometa).
    /// Controlla flag espliciti (MTFLAG) o la presenza di tassi di moto (Rates).
    /// 
    /// </summary>
    public bool IsMovingTarget(FitsHeader header)
    {
        // 1. Check Flag Hubble/Standard [cite: 172]
        string mtFlag = GetStringValue(header, "MTFLAG");
        if (mtFlag == "T") return true;

        // 2. Check differenziali di moto (Rates) [cite: 184]
        // Spesso presenti nei commenti o chiavi non standard come RA_RATE/DEC_RATE
        double raRate = GetDoubleValue(header, "RA_RATE");
        double decRate = GetDoubleValue(header, "DEC_RATE");

        // Se i tassi sono significativamente diversi da zero, è tracking non-siderale
        return Math.Abs(raRate) > 1e-9 || Math.Abs(decRate) > 1e-9;
    }

    // =======================================================================
    // FINE NUOVA IMPLEMENTAZIONE
    // =======================================================================

    public double? GetFocalLength(FitsHeader header) => OpticalParser.ParseFocalLength(header, this);
    public double? GetPixelSize(FitsHeader header) => OpticalParser.ParsePixelSize(header, this);
    public WcsData ExtractWcs(FitsHeader header) => WcsParser.Parse(header, this);
    public GeographicLocation? GetObservatoryLocation(FitsHeader header) => GeographicParser.ParseLocation(header, this);

    public FitsBitDepth GetBitDepth(FitsHeader header)
    {
        return GetIntValue(header, "BITPIX") switch
        {
            8 => FitsBitDepth.UInt8, 16 => FitsBitDepth.Int16, 32 => FitsBitDepth.Int32,
            -32 => FitsBitDepth.Float, -64 => FitsBitDepth.Double, _ => FitsBitDepth.Int16
        };
    }
    
    public SkyCoordinate? GetTargetCoordinates(FitsHeader header)
    {
        if (header == null) return null;
        var raCard = header.Cards.FirstOrDefault(c => c.Key.Equals("RA", StringComparison.OrdinalIgnoreCase)) ?? 
                     header.Cards.FirstOrDefault(c => c.Key.Equals("OBJCTRA", StringComparison.OrdinalIgnoreCase));
        var decCard = header.Cards.FirstOrDefault(c => c.Key.Equals("DEC", StringComparison.OrdinalIgnoreCase)) ?? 
                      header.Cards.FirstOrDefault(c => c.Key.Equals("OBJCTDEC", StringComparison.OrdinalIgnoreCase));

        if (raCard == null || decCard == null) return null;

        string raValRaw = GetStringValue(header, raCard.Key);
        string raComment = raCard.Comment?.ToLowerInvariant() ?? "";
        
        double raDeg = 0;
        if (double.TryParse(raValRaw, NumberStyles.Any, CultureInfo.InvariantCulture, out double rawRa))
            raDeg = (raComment.Contains("deg") || rawRa > 24) ? rawRa : rawRa * 15.0;
        else
            raDeg = AstroParser.ParseHoursToDegrees(raValRaw) ?? 0;

        string decValRaw = GetStringValue(header, decCard.Key);
        double decDeg = AstroParser.ParseDegrees(decValRaw) ?? 0;

        return new SkyCoordinate(raDeg, decDeg);
    }
    
    // --- SCRITTURA / MANIPOLAZIONE (CORRETTA) ---

    private void InsertCardRaw(FitsHeader header, FitsCard card)
    {
        if (!AdditiveKeys.Contains(card.Key))
        {
            header.RemoveCard(card.Key);
        }
        header.AddCard(card);
    }

    private void EnforceEndKey(FitsHeader header)
    {
        while (header.Cards.Any(c => c.Key.Equals("END", StringComparison.OrdinalIgnoreCase)))
        {
            header.RemoveCard("END");
        }
        header.AddCard(new FitsCard("END", string.Empty, string.Empty, false));
    }

    public void AddValue(FitsHeader header, string key, object value, string? comment = null)
    {
        header.AddCard(CreateCard(key, value, comment));
        EnforceEndKey(header);
    }

    public void SetValue(FitsHeader header, string key, object value, string? comment = null)
    {
        InsertCardRaw(header, CreateCard(key, value, comment));
        EnforceEndKey(header);
    }

    public void TransferMetadata(FitsHeader source, FitsHeader destination)
    {
        if (source == null || destination == null) return;
        RemoveEndKey(destination);

        foreach (var card in source.Cards)
        {
            if (StructuralKeys.Contains(card.Key)) continue;
            InsertCardRaw(destination, card);
        }
        EnforceEndKey(destination);
    }

    public FitsHeader CreateHeaderFromTemplate(FitsHeader template, Array newPixels, FitsBitDepth depth)
    {
        var newHeader = new FitsHeader();
        
        SetValue(newHeader, "SIMPLE", true, "Standard FITS format");
        SetValue(newHeader, "BITPIX", (int)depth, GetBitpixComment((int)depth));
        SetValue(newHeader, "NAXIS", 2, "2D Image");
        SetValue(newHeader, "NAXIS1", newPixels.GetLength(1), "Image Width");
        SetValue(newHeader, "NAXIS2", newPixels.GetLength(0), "Image Height");
        
        // Uso di DATE generico per creazione file, non confondibile con DATE-OBS
        SetValue(newHeader, "DATE", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss"), "File creation date (UTC)");
        SetValue(newHeader, "CREATOR", "KomaLab v0.4", "Software used to create this file");
        
        SetValue(newHeader, "BSCALE", 1.0);
        SetValue(newHeader, "BZERO", 0.0);
        
        if (template != null) TransferMetadata(template, newHeader);
        EnforceEndKey(newHeader);
        return newHeader;
    }

    // --- ALTRI METODI (Invariati) ---

    public FitsBitDepth ResolveOutputBitDepth(FitsBitDepth original, bool hasSpecialValues)
    {
        if ((int)original < 0) return original;
        if (hasSpecialValues) return (original == FitsBitDepth.Int32) ? FitsBitDepth.Double : FitsBitDepth.Float;
        return original;
    }

    public void ShiftWcs(FitsHeader header, double deltaX, double deltaY)
    {
        if (header == null || (deltaX == 0 && deltaY == 0)) return;
        double crpix1 = GetDoubleValue(header, "CRPIX1");
        double crpix2 = GetDoubleValue(header, "CRPIX2");

        if (Math.Abs(crpix1) > 0.0001 || Math.Abs(crpix2) > 0.0001)
        {
            SetValue(header, "CRPIX1", crpix1 + deltaX);
            SetValue(header, "CRPIX2", crpix2 - deltaY);
        }
    }

    public IEnumerable<T> SortByDate<T>(IEnumerable<T> items, Func<T, FitsHeader?> headerSelector)
    {
        if (items == null) return Enumerable.Empty<T>();
        // Usa il GetObservationDate potenziato che supporta i millisecondi
        return items.OrderBy(item => GetObservationDate(headerSelector(item)!) ?? DateTime.MinValue);
    }
    
    public FitsHeader CloneHeader(FitsHeader header) => header?.Clone() ?? new FitsHeader();

    private void RemoveEndKey(FitsHeader header)
    {
        while (header.Cards.Any(c => c.Key.Equals("END", StringComparison.OrdinalIgnoreCase)))
        {
            header.RemoveCard("END");
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
        return $"'{s.Replace("'", "''"),-8}'"; 
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