using System;
using System.Collections.Generic;
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
        var checks = new List<HealthStatusItem>();

        if (header == null) return new HeaderHealthReport(checks);

        // Esecuzione dei 5 pilastri della salute FITS
        checks.Add(EvaluateTime(header));
        checks.Add(EvaluateLocation(header));
        checks.Add(EvaluateOptics(header));
        checks.Add(EvaluateTarget(header));
        checks.Add(EvaluateWcs(header));

        return new HeaderHealthReport(checks);
    }

    private HealthStatusItem EvaluateTime(FitsHeader header)
    {
        var dt = _metadataService.GetObservationDate(header);
        return new HealthStatusItem(
            HealthCheckType.TimeReference,
            dt.HasValue ? HeaderHealthStatus.Valid : HeaderHealthStatus.Invalid,
            dt.HasValue ? $"{dt.Value:yyyy-MM-dd HH:mm:ss}" : "Chiave DATE-OBS mancante.");
    }

    private HealthStatusItem EvaluateLocation(FitsHeader header)
    {
        var loc = _metadataService.GetObservatoryLocation(header);
        return new HealthStatusItem(
            HealthCheckType.ObservatoryLocation,
            loc != null ? HeaderHealthStatus.Valid : HeaderHealthStatus.Invalid,
            loc != null ? $"{loc.Latitude:F3}°, {loc.Longitude:F3}°" : "Coordinate SITELAT/LONG assenti.");
    }

    private HealthStatusItem EvaluateOptics(FitsHeader header)
    {
        var focal = _metadataService.GetFocalLength(header);
        var pix = _metadataService.GetPixelSize(header);
        bool ok = focal.HasValue && pix.HasValue;
        
        return new HealthStatusItem(
            HealthCheckType.OpticalConfiguration,
            ok ? HeaderHealthStatus.Valid : HeaderHealthStatus.Invalid,
            ok ? $"{focal:F0}mm | {pix:F2}µm" : "FOCALLEN o PIXSIZE mancanti.");
    }

    private HealthStatusItem EvaluateTarget(FitsHeader header)
    {
        var coords = _metadataService.GetTargetCoordinates(header);
        bool ok = coords != null;

        return new HealthStatusItem(
            HealthCheckType.TargetPointers,
            ok ? HeaderHealthStatus.Valid : HeaderHealthStatus.Warning,
            ok ? $"Coordinate: {coords!.RaDeg:F3}°, {coords.DecDeg:F3}°" : "Coordinate RA/DEC mancanti.");
    }

    private HealthStatusItem EvaluateWcs(FitsHeader header)
    {
        var wcs = _metadataService.ExtractWcs(header);
        bool solved = wcs is { IsValid: true };
        
        return new HealthStatusItem(
            HealthCheckType.AstrometricSolution,
            solved ? HeaderHealthStatus.Valid : HeaderHealthStatus.Pending,
            solved ? $"Scala: {wcs.PixelScaleArcsec:F2}\"/px" : "Dati WCS non trovati.");
    }
}