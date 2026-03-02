using System.Collections.Generic;

namespace Kometra.Models.Fits.Health;

public enum HealthCheckType 
{ 
    TimeReference, 
    ObservatoryLocation, 
    OpticalConfiguration, 
    TargetPointers, 
    AstrometricSolution 
}

public record HealthStatusItem(
    HealthCheckType Type, 
    HeaderHealthStatus Status, 
    string Message);

public record HeaderHealthReport(List<HealthStatusItem> Checks);