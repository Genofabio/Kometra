using System;
using KomaLab.Models.Fits;
using KomaLab.Models.Fits.Health;
using KomaLab.Models.Fits.Structure;

namespace KomaLab.Services.Fits.Metadata;

public class FitsHeaderHealthEvaluator : IFitsHeaderHealthEvaluator
{
    private readonly IFitsMetadataService _metadataService;

    public FitsHeaderHealthEvaluator(IFitsMetadataService metadataService)
    {
        _metadataService = metadataService ?? throw new ArgumentNullException(nameof(metadataService));
    }

    public HeaderHealthReport Evaluate(FitsHeader header)
    {
        if (header == null) return CreateEmptyReport();

        return new HeaderHealthReport(
            Date: EvaluateDate(header),
            Location: EvaluateLocation(header),
            Wcs: EvaluateWcs(header)
        );
    }

    // --- Logiche di analisi (Private) ---

    private HealthStatusItem EvaluateDate(FitsHeader header)
    {
        var dt = _metadataService.GetObservationDate(header);
        
        return dt.HasValue 
            ? new HealthStatusItem(HeaderHealthStatus.Valid, $"Data: {dt.Value:yyyy-MM-dd HH:mm:ss}")
            : new HealthStatusItem(HeaderHealthStatus.Invalid, "Timestamp mancante o formato non riconosciuto.");
    }

    private HealthStatusItem EvaluateLocation(FitsHeader header)
    {
        var loc = _metadataService.GetObservatoryLocation(header);
        
        return loc != null 
            ? new HealthStatusItem(HeaderHealthStatus.Valid, $"Sito: {loc.Latitude:F3}, {loc.Longitude:F3}")
            : new HealthStatusItem(HeaderHealthStatus.Invalid, "Coordinate sito assenti (SITELAT/LONG).");
    }

    private HealthStatusItem EvaluateWcs(FitsHeader header)
    {
        var wcs = _metadataService.ExtractWcs(header);
        
        return wcs is { IsValid: true } 
            ? new HealthStatusItem(HeaderHealthStatus.Valid, $"WCS OK (Scala: {wcs.PixelScaleArcsec:F2}\"/px)")
            : new HealthStatusItem(HeaderHealthStatus.Invalid, "Dati WCS mancanti o calibrazione non valida.");
    }

    private HeaderHealthReport CreateEmptyReport()
    {
        var pending = new HealthStatusItem(HeaderHealthStatus.Pending, "Nessun dato.");
        return new HeaderHealthReport(pending, pending, pending);
    }
}