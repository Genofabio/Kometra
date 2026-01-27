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
    double ImageHeight,
    
    // [SCIENTIFIC FIX]
    // Indica se il telescopio stava inseguendo un oggetto non siderale (MTFLAG=T o Rates != 0).
    // Se True, le stelle saranno strisciate e l'allineamento stellare potrebbe fallire.
    bool IsTracked = false 
);