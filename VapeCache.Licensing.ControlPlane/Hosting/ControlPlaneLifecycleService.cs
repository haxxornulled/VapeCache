using Microsoft.Extensions.Hosting;
using VapeCache.Licensing.ControlPlane.Revocation;

namespace VapeCache.Licensing.ControlPlane.Hosting;

/// <summary>
/// Emits startup lifecycle diagnostics for revocation state.
/// </summary>
public sealed class ControlPlaneLifecycleService(
    IRevocationRegistry registry,
    ILogger<ControlPlaneLifecycleService> logger) : IHostedLifecycleService
{
    /// <inheritdoc />
    public Task StartingAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <inheritdoc />
    public Task StartedAsync(CancellationToken cancellationToken)
    {
        var snapshot = registry.GetSnapshot();
        logger.LogInformation(
            "Licensing control-plane started. RevokedLicenses={Licenses} KilledOrganizations={Organizations} RevokedKeyIds={KeyIds}",
            snapshot.RevokedLicenses.Count,
            snapshot.OrganizationKillSwitches.Count,
            snapshot.RevokedKeyIds.Count);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StoppingAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <inheritdoc />
    public Task StoppedAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;
}
