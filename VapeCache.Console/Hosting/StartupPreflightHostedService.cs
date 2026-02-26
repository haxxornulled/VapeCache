using System.Buffers;
using System.Diagnostics;
using LanguageExt;
using LanguageExt.Common;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Connections;

namespace VapeCache.Console.Hosting;

internal sealed class StartupPreflightHostedService(
    IOptions<StartupPreflightOptions> options,
    IRedisConnectionFactory factory,
    IRedisFailoverController failover,
    ICurrentCacheService current,
    ILogger<StartupPreflightHostedService> logger) : IHostedLifecycleService
{
    // RESP: *1\r\n$4\r\nPING\r\n
    private static readonly byte[] Ping = "*1\r\n$4\r\nPING\r\n"u8.ToArray();

    /// <summary>
    /// Executes value.
    /// </summary>
    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Executes value.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var o = options.Value;
        if (!o.Enabled) return;

        if (o.Connections <= 0)
            throw new InvalidOperationException("StartupPreflight:Connections must be > 0 when enabled.");

        logger.LogInformation(
            "Startup preflight enabled. Connections={Connections} Timeout={Timeout} ValidatePing={ValidatePing} FailFast={FailFast}",
            o.Connections,
            o.Timeout,
            o.ValidatePing,
            o.FailFast);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (o.Timeout > TimeSpan.Zero)
            cts.CancelAfter(o.Timeout);

        var sw = Stopwatch.StartNew();

        try
        {
            var tasks = new Task<Result<Unit>>[o.Connections];
            for (var i = 0; i < tasks.Length; i++)
                tasks[i] = CreateAndValidateAsync(o, cts.Token).AsTask();

            await Task.WhenAll(tasks).ConfigureAwait(false);

            var failures = 0;
            foreach (var t in tasks)
                if (!t.Result.IsSuccess) failures++;

            sw.Stop();

            if (failures == 0)
            {
                logger.LogInformation("Startup preflight succeeded in {Ms}ms.", sw.Elapsed.TotalMilliseconds);
                return;
            }

            logger.LogWarning(
                "Startup preflight failed ({Failures}/{Total}) in {Ms}ms.",
                failures,
                o.Connections,
                sw.Elapsed.TotalMilliseconds);

            if (o.FailFast)
                throw new InvalidOperationException($"Startup preflight failed ({failures}/{o.Connections}).");

            if (o.FailoverToMemoryOnFailure)
            {
                failover.ForceOpen("startup-preflight-failed");
                current.SetCurrent("memory");
                logger.LogWarning("Forcing Redis off (memory-only) until sanity check succeeds.");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            sw.Stop();
            logger.LogWarning("Startup preflight timed out after {Ms}ms.", sw.Elapsed.TotalMilliseconds);
            if (o.FailFast)
                throw new TimeoutException($"Startup preflight timed out after {sw.Elapsed.TotalMilliseconds:0.0}ms.");

            if (o.FailoverToMemoryOnFailure)
            {
                failover.ForceOpen("startup-preflight-timeout");
                current.SetCurrent("memory");
                logger.LogWarning("Forcing Redis off (memory-only) until sanity check succeeds.");
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            sw.Stop();
            logger.LogError(ex, "Startup preflight failed after {Ms}ms.", sw.Elapsed.TotalMilliseconds);
            if (o.FailFast)
                throw;

            if (o.FailoverToMemoryOnFailure)
            {
                failover.ForceOpen("startup-preflight-exception");
                current.SetCurrent("memory");
                logger.LogWarning("Forcing Redis off (memory-only) until sanity check succeeds.");
            }
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Executes value.
    /// </summary>
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Executes value.
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Executes value.
    /// </summary>
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async ValueTask<Result<Unit>> CreateAndValidateAsync(StartupPreflightOptions o, CancellationToken ct)
    {
        var created = await factory.CreateAsync(ct).ConfigureAwait(false);
        if (!created.IsSuccess)
            return created.Match(static _ => Prelude.unit, static ex => new Result<Unit>(ex));

        var conn = created.Match(static succ => succ, static ex => throw ex);
        await using var _ = conn.ConfigureAwait(false);

        if (!o.ValidatePing)
            return Prelude.unit;

        var ok = await TryPingAsync(conn.Stream, ct).ConfigureAwait(false);
        return ok ? Prelude.unit : new Result<Unit>(new InvalidOperationException("PING failed."));
    }

    private static async ValueTask<bool> TryPingAsync(Stream stream, CancellationToken ct)
    {
        try
        {
            await stream.WriteAsync(Ping, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);

            // Read RESP simple string line: +PONG\r\n
            var line = await ReadLineAsync(stream, ct).ConfigureAwait(false);
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
                {
                    // Very defensive: line too long (shouldn't happen for PONG).
                    throw new InvalidOperationException("RESP line too long.");
                }
            }
        }
        finally
        {
            if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
