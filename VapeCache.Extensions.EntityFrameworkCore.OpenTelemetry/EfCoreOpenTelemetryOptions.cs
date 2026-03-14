namespace VapeCache.Extensions.EntityFrameworkCore.OpenTelemetry;

/// <summary>
/// Options for EF Core OpenTelemetry observer behavior.
/// </summary>
public sealed class EfCoreOpenTelemetryOptions
{
    /// <summary>
    /// Enables this telemetry observer.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Emits lightweight activities for profiler/event correlation.
    /// </summary>
    public bool EmitActivities { get; set; } = true;
}
