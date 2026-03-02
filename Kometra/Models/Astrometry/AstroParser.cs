using System;
using System.Globalization;

namespace Kometra.Models.Astrometry;

/// <summary>
/// Utility universale per il parsing di coordinate astronomiche e geografiche.
/// Centralizza la logica di conversione sessagesimale per tutto il dominio.
/// </summary>
public static class AstroParser
{
    private static readonly char[] Separators = { ':', 'd', 'm', 's', 'h', '°', '\'', '"', ' ' };

    /// <summary>
    /// Parsa una stringa in gradi (es. "45:30:00", "45.5", "45d 30m").
    /// </summary>
    public static double? ParseDegrees(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
    
        input = input.Trim();
    
        // 1. Caso decimale puro (es. "45.1234")
        if (double.TryParse(input, NumberStyles.Any, CultureInfo.InvariantCulture, out double val)) 
            return val;
    
        // 2. Caso sessagesimale (Gradi:Minuti:Secondi o 45° 30' 00")
        try 
        {
            // Usiamo i separatori per dividere, mantenendo l'integrità dei numeri
            string[] parts = input.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return null;
    
            double d = double.Parse(parts[0], CultureInfo.InvariantCulture);
            double m = parts.Length > 1 ? double.Parse(parts[1], CultureInfo.InvariantCulture) : 0;
            double s = parts.Length > 2 ? double.Parse(parts[2], CultureInfo.InvariantCulture) : 0;
            
            double res = Math.Abs(d) + (m / 60.0) + (s / 3600.0);
            
            // Gestione del segno: cerchiamo '-' o direzioni Sud/Ovest
            bool isNegative = input.Contains("-") || 
                              input.Contains("S", StringComparison.OrdinalIgnoreCase) || 
                              input.Contains("W", StringComparison.OrdinalIgnoreCase);
    
            return isNegative ? -res : res;
        }
        catch 
        { 
            return null; 
        }
    }

    /// <summary>
    /// Parsa una stringa in ore e la converte in gradi (1h = 15°). 
    /// Tipico per l'Ascensione Retta (RA).
    /// </summary>
    public static double? ParseHoursToDegrees(string? input)
    {
        double? hours = ParseDegrees(input);
        return hours.HasValue ? hours.Value * 15.0 : null;
    }
}