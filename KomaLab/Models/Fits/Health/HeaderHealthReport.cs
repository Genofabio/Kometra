namespace KomaLab.Models.Fits.Health;

/// <summary>
/// Rappresenta il risultato granulare di un controllo (Stato + Messaggio descrittivo).
/// </summary>
public record HealthStatusItem(HeaderHealthStatus Status, string Message);

/// <summary>
/// Report complessivo della "salute" di un header FITS.
/// Raggruppa le analisi per aree tematiche fondamentali.
/// </summary>
public record HeaderHealthReport(
    HealthStatusItem Date,
    HealthStatusItem Location,
    HealthStatusItem Wcs
);