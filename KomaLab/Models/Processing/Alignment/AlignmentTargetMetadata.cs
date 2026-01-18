using System;
using KomaLab.Models.Astrometry;

namespace KomaLab.Models.Processing;

/// <summary>
/// Pacchetto di informazioni "pulite" estratte dai metadati FITS
/// specificamente per il workflow di allineamento.
/// </summary>
public record AlignmentTargetMetadata(
    string ObjectName,
    DateTime? ObservationDate,
    GeographicLocation? Location,
    bool HasWcs,
    double ImageWidth,
    double ImageHeight
);