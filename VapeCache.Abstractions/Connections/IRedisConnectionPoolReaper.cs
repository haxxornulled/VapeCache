namespace VapeCache.Abstractions.Connections;

public interface IRedisConnectionPoolReaper
{
    Task RunReaperAsync(CancellationToken ct);
}
