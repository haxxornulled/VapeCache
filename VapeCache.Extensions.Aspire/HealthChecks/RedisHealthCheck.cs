using Microsoft.Extensions.Diagnostics.HealthChecks;
using VapeCache.Abstractions.Connections;

namespace VapeCache.Extensions.Aspire.HealthChecks;

public sealed class RedisHealthCheck : IHealthCheck
{
    private static readonly ReadOnlyMemory<byte> PingCommand = "*1\r\n$4\r\nPING\r\n"u8.ToArray();
    private static readonly byte[] PongResponse = "+PONG\r\n"u8.ToArray();
    private readonly IRedisConnectionPool _pool;

    public RedisHealthCheck(IRedisConnectionPool pool)
    {
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _pool.RentAsync(cancellationToken);

            return await result.Match(
                async lease =>
                {
                    await using var leasedConnection = lease;
                    await leasedConnection.Connection.Stream.WriteAsync(PingCommand, cancellationToken).ConfigureAwait(false);
                    await leasedConnection.Connection.Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    await ExpectPongAsync(leasedConnection.Connection.Stream, cancellationToken).ConfigureAwait(false);
                    return HealthCheckResult.Healthy("Redis responded to PING.");
                },
                error =>
                {
                    if (error is TimeoutException timeout)
                    {
                        return Task.FromResult(HealthCheckResult.Degraded(
                            "Redis connection pool timed out while acquiring a connection.",
                            exception: timeout,
                            data: new Dictionary<string, object>
                            {
                                ["reason"] = "pool_timeout"
                            }));
                    }

                    return Task.FromResult(HealthCheckResult.Unhealthy(
                        "Redis connection pool failed to acquire a usable connection.",
                        exception: error,
                        data: new Dictionary<string, object>
                        {
                            ["error_type"] = error.GetType().Name
                        }));
                });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy("Health check cancelled.");
        }
        catch (TimeoutException ex)
        {
            return HealthCheckResult.Degraded(
                "Redis connection pool timeout.",
                exception: ex,
                data: new Dictionary<string, object> { ["reason"] = "pool_timeout" });
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy(
                "Redis is unavailable.",
                exception: ex,
                data: new Dictionary<string, object>
                {
                    ["reason"] = "connection_failed",
                    ["error_type"] = ex.GetType().Name
                });
        }
    }

    private static async Task ExpectPongAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[PongResponse.Length];
        var read = 0;
        while (read < buffer.Length)
        {
            var received = await stream.ReadAsync(buffer.AsMemory(read, buffer.Length - read), cancellationToken).ConfigureAwait(false);
            if (received == 0)
                throw new EndOfStreamException("Redis health check received EOF before PONG.");
            read += received;
        }

        if (!buffer.AsSpan().SequenceEqual(PongResponse))
            throw new InvalidOperationException("Redis health check received an unexpected PING response.");
    }
}
