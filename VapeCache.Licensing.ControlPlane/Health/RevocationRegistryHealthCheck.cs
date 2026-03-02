using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using VapeCache.Licensing.ControlPlane.Revocation;

namespace VapeCache.Licensing.ControlPlane.Health;

/// <summary>
/// Readiness check for the revocation registry and its persistence location.
/// </summary>
public sealed class RevocationRegistryHealthCheck(
    IRevocationRegistry registry,
    IOptionsMonitor<RevocationControlPlaneOptions> optionsMonitor) : IHealthCheck
{
    /// <summary>
    /// Executes the health check.
    /// </summary>
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromResult(HealthCheckResult.Unhealthy("Health check cancelled."));

        try
        {
            var snapshot = registry.GetSnapshot();
            var path = ResolveStatePath(optionsMonitor.CurrentValue.PersistencePath);
            var directory = Path.GetDirectoryName(path);

            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return Task.FromResult(HealthCheckResult.Unhealthy(
                    "Revocation registry persistence directory is unavailable.",
                    data: new Dictionary<string, object>
                    {
                        ["path"] = path
                    }));
            }

            if (File.Exists(path))
            {
                using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
            }

            return Task.FromResult(HealthCheckResult.Healthy(
                "Revocation registry is ready.",
                new Dictionary<string, object>
                {
                    ["path"] = path,
                    ["state_file_present"] = File.Exists(path),
                    ["licenses"] = snapshot.RevokedLicenses.Count,
                    ["organizations"] = snapshot.OrganizationKillSwitches.Count,
                    ["key_ids"] = snapshot.RevokedKeyIds.Count
                }));
        }
        catch (Exception ex)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "Revocation registry is not operational.",
                exception: ex,
                data: new Dictionary<string, object>
                {
                    ["error_type"] = ex.GetType().Name
                }));
        }
    }

    private static string ResolveStatePath(string configuredPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredPath);
        return Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configuredPath));
    }
}
