using System.Collections.Generic;

namespace Kometra.Models.Astrometry.Solving;

// Enum che definisce COSA manca, non COME dirlo.
public enum AstrometryPrerequisite
{
    ApproximatePosition, // Mancano RA/DEC
    FocalLength,         // Manca Focale
    PixelSize            // Manca dimensione Pixel
}

public class AstrometryDiagnosis
{
    public List<AstrometryPrerequisite> MissingItems { get; } = new();

    // Scorciatoia per capire se è tutto ok
    public bool IsReady => MissingItems.Count == 0;
}