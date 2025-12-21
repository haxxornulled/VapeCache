namespace VapeCache.Application.Connections;

public interface IRedisConnectionPoolReaper
{
    Task RunReaperAsync(CancellationToken ct);
}

