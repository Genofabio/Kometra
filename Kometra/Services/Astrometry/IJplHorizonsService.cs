using System;
using System.Threading;
using System.Threading.Tasks;
using Kometra.Models.Astrometry;

namespace Kometra.Services.Astrometry;

public interface IJplHorizonsService
{
    Task<(double Ra, double Dec)?> GetEphemerisAsync(
        string objectName, 
        DateTime observationTime, 
        GeographicLocation? observerLocation = null,
        CancellationToken token = default);
}