namespace VapeCache.Abstractions.Connections;

/// <summary>
/// Defines the redis connection pool reaper contract.
/// </summary>
public interface IRedisConnectionPoolReaper
{
    /// <summary>
    /// Executes run reaper async.
    /// </summary>
    Task RunReaperAsync(CancellationToken ct);
}
