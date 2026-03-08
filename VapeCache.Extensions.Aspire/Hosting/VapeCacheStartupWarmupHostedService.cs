using LanguageExt.Common;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Connections;

namespace VapeCache.Extensions.Aspire.Hosting;

internal sealed partial class VapeCacheStartupWarmupHostedService(
    IOptions<VapeCacheStartupWarmupOptions> options,
    IRedisConnectionPool pool,
    IVapeCacheStartupReadiness readiness,
    ILogger<VapeCacheStartupWarmupHostedService> logger) : IHostedService
{
    private static readonly ReadOnlyMemory<byte> PingCommand = "*1\r\n$4\r\nPING\r\n"u8.ToArray();
    private static ReadOnlySpan<byte> PongResponse => "+PONG\r\n"u8;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var o = options.Value;
        if (!o.Enabled)
        {
            readiness.MarkWarmupDisabled();
            return;
        }

        var targetConnections = Math.Max(1, o.ConnectionsToWarm);
        var requiredSuccess = Math.Clamp(o.RequiredSuccessfulConnections, 1, targetConnections);

        readiness.MarkRunning(targetConnections);
        LogWarmupStarting(logger, targetConnections, requiredSuccess, o.ValidatePing);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(o.Timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : o.Timeout);
        var ct = timeoutCts.Token;

        var successes = 0;
        var failures = 0;
        Exception? lastError = null;

        for (var i = 0; i < targetConnections; i++)
        {
            if (ct.IsCancellationRequested)
                break;

            var warmed = await WarmConnectionAsync(o.ValidatePing, ct).ConfigureAwait(false);
            if (warmed.IsSuccess)
            {
                successes++;
                continue;
            }

            failures++;
            warmed.IfFail(ex => lastError = ex);
        }

        var ready = successes >= requiredSuccess && failures == 0;
        var status = ready
            ? $"startup-warmup-ready ({successes}/{targetConnections})"
            : $"startup-warmup-degraded ({successes}/{targetConnections}, failures={failures})";

        readiness.MarkCompleted(
            ready: ready,
            successfulConnections: successes,
            failedConnections: failures,
            status: status,
            lastError: lastError);

        if (ready)
        {
            LogWarmupSucceeded(logger, successes, targetConnections);
            return;
        }

        LogWarmupFailed(logger, successes, targetConnections, failures, lastError);
        if (o.FailFastOnWarmupFailure)
        {
            throw new InvalidOperationException(
                $"VapeCache startup warmup failed ({successes}/{targetConnections}, failures={failures}).",
                lastError);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async ValueTask<Result<bool>> WarmConnectionAsync(bool validatePing, CancellationToken cancellationToken)
    {
        var leaseResult = await pool.RentAsync(cancellationToken).ConfigureAwait(false);
        return await leaseResult.Match(
            async lease =>
            {
                await using var leasedConnection = lease;
                if (!validatePing)
                    return new Result<bool>(true);

                return await ValidatePingAsync(leasedConnection.Connection, cancellationToken).ConfigureAwait(false);
            },
            error => Task.FromResult(new Result<bool>(error))).ConfigureAwait(false);
    }

    private static async ValueTask<Result<bool>> ValidatePingAsync(IRedisConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            var send = await connection.SendAsync(PingCommand, cancellationToken).ConfigureAwait(false);
            if (!send.IsSuccess)
                return send.Match(_ => new Result<bool>(true), ex => new Result<bool>(ex));

            var pong = new byte[7];
            var read = 0;

            while (read < pong.Length)
            {
                var receivedResult = await connection.ReceiveAsync(pong.AsMemory(read, pong.Length - read), cancellationToken).ConfigureAwait(false);
                if (!receivedResult.IsSuccess)
                    return receivedResult.Match(_ => new Result<bool>(true), ex => new Result<bool>(ex));

                var received = receivedResult.Match(v => v, _ => 0);
                if (received <= 0)
                    return new Result<bool>(new EndOfStreamException("Redis warmup received EOF before PONG."));

                read += received;
            }

            if (!pong.AsSpan().SequenceEqual(PongResponse))
                return new Result<bool>(new InvalidOperationException("Redis warmup received an unexpected PING response."));

            return new Result<bool>(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new Result<bool>(ex);
        }
    }

    [LoggerMessage(
        EventId = 18000,
        Level = LogLevel.Information,
        Message = "VapeCache startup warmup starting. Connections={Connections} RequiredSuccess={RequiredSuccess} ValidatePing={ValidatePing}")]
    private static partial void LogWarmupStarting(ILogger logger, int connections, int requiredSuccess, bool validatePing);

    [LoggerMessage(
        EventId = 18001,
        Level = LogLevel.Information,
        Message = "VapeCache startup warmup completed successfully. Successes={Successes}/{Connections}")]
    private static partial void LogWarmupSucceeded(ILogger logger, int successes, int connections);

    [LoggerMessage(
        EventId = 18002,
        Level = LogLevel.Warning,
        Message = "VapeCache startup warmup completed with failures. Successes={Successes}/{Connections} Failures={Failures}")]
    private static partial void LogWarmupFailed(ILogger logger, int successes, int connections, int failures, Exception? exception);
}
