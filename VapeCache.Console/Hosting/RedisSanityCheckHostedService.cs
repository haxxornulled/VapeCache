using System.Buffers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Connections;

namespace VapeCache.Console.Hosting;

internal sealed class RedisSanityCheckHostedService(
    IOptions<StartupPreflightOptions> options,
    IRedisConnectionFactory factory,
    IRedisFailoverController failover,
    ILogger<RedisSanityCheckHostedService> logger) : BackgroundService, IHostedLifecycleService
{
    private static readonly byte[] Ping = "*1\r\n$4\r\nPING\r\n"u8.ToArray();

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        if (!o.Enabled || !o.SanityCheckEnabled)
            return;

        if (o.SanityCheckInterval <= TimeSpan.Zero)
            return;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(o.SanityCheckInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            if (!failover.IsForcedOpen)
                continue;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            if (o.SanityCheckTimeout > TimeSpan.Zero)
                cts.CancelAfter(o.SanityCheckTimeout);

            var retries = Math.Max(0, o.SanityCheckRetries);
            var delay = o.SanityCheckRetryDelay <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(250) : o.SanityCheckRetryDelay;

            var ok = await Policy
                .Handle<Exception>()
                .WaitAndRetryAsync(retries, _ => delay)
                .ExecuteAsync(async token => await TryConnectAndPingAsync(token).ConfigureAwait(false), cts.Token)
                .ConfigureAwait(false);

            if (!ok)
                continue;

            failover.ClearForcedOpen();
            logger.LogInformation("Redis sanity check succeeded; Redis re-enabled.");
        }
    }

    private async ValueTask<bool> TryConnectAndPingAsync(CancellationToken ct)
    {
        var created = await factory.CreateAsync(ct).ConfigureAwait(false);
        if (!created.IsSuccess)
            return false;

        var conn = created.Match(static succ => succ, static _ => null!);
        if (conn is null) return false;

        await using var _ = conn.ConfigureAwait(false);
        try
        {
            await conn.Stream.WriteAsync(Ping, ct).ConfigureAwait(false);
            await conn.Stream.FlushAsync(ct).ConfigureAwait(false);
            var line = await ReadLineAsync(conn.Stream, ct).ConfigureAwait(false);
            return string.Equals(line, "+PONG", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static async ValueTask<string> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        byte[]? rented = null;
        try
        {
            rented = ArrayPool<byte>.Shared.Rent(256);
            var idx = 0;
            while (true)
            {
                var read = await stream.ReadAsync(rented.AsMemory(idx, 1), ct).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException();
                idx += read;

                if (idx >= 2 && rented[idx - 2] == (byte)'\r' && rented[idx - 1] == (byte)'\n')
                    return System.Text.Encoding.ASCII.GetString(rented, 0, idx - 2);

                if (idx == rented.Length)
                    throw new InvalidOperationException("RESP line too long.");
            }
        }
        finally
        {
            if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
