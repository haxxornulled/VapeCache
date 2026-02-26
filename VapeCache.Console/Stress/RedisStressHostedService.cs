using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Connections;
using System.Security.Cryptography;
using VapeCache.Abstractions.Caching;

namespace VapeCache.Console.Stress;

internal sealed class RedisStressHostedService(
    IOptions<RedisStressOptions> stressOptions,
    IOptionsMonitor<RedisConnectionOptions> redisOptions,
    IRedisConnectionFactory factory,
    IRedisConnectionPool pool,
    IRedisCommandExecutor executor,
    IHostApplicationLifetime lifetime,
    ILogger<RedisStressHostedService> logger) : IHostedLifecycleService
{
    private CancellationTokenSource? _cts;
    private Task? _runTask;

    /// <summary>
    /// Executes value.
    /// </summary>
    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Executes value.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (!stressOptions.Value.Enabled)
            return Task.CompletedTask;
        _runTask = RunAsync(_cts.Token);
        return Task.CompletedTask;
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
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is not null)
        {
            try { _cts.Cancel(); } catch { }
            _cts.Dispose();
        }

        if (_runTask is not null)
        {
            try { await _runTask.WaitAsync(cancellationToken).ConfigureAwait(false); } catch { }
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task RunAsync(CancellationToken ct)
    {
        var stress = stressOptions.Value;
        var mode = (stress.Mode ?? "pool").Trim().ToLowerInvariant();
        var operationTimeout = stress.OperationTimeout;
        var workload = (stress.Workload ?? "ping").Trim().ToLowerInvariant();
        TokenBucketPacer? limiter = null;
        if (stress.TargetRps > 0)
            limiter = new TokenBucketPacer(stress.TargetRps, Math.Max(1, stress.BurstRequests));

        DateTimeOffset? deadline = null;
        if (stress.Duration > TimeSpan.Zero && !(mode == "burn" && stress.BurnConnectionsTarget > 0))
            deadline = DateTimeOffset.UtcNow.Add(stress.Duration);

        var modePool = mode == "pool" ? pool : null;
        var modeExecutor = mode == "mux" ? executor : null;

        string[]? muxKeys = null;
        if (mode == "mux" && workload == "payload")
        {
            var users = Math.Max(1, stress.VirtualUsers);
            muxKeys = new string[users];
            for (var i = 0; i < users; i++)
                muxKeys[i] = $"{stress.KeyPrefix}{i}";

            if (stress.PreloadKeys)
            {
                logger.LogInformation("Preloading mux keys: Count={Count} PayloadBytes={PayloadBytes} Ttl={Ttl}", muxKeys.Length, stress.PayloadBytes, stress.PayloadTtl);
                await PreloadMuxKeysAsync(modeExecutor!, muxKeys, stress.PayloadBytes, stress.PayloadTtl, Math.Max(1, stress.Workers), ct).ConfigureAwait(false);
                logger.LogInformation("Preload complete.");
            }
        }

        logger.LogInformation(
            "Redis stress starting: Mode={Mode} Workload={Workload} Workers={Workers} Duration={Duration} PayloadBytes={PayloadBytes} KeySpace={KeySpace} SetPercent={SetPercent} OpsPerLease={OpsPerLease} BurnTarget={BurnTarget}",
            mode,
            workload,
            stress.Workers,
            stress.Duration,
            stress.PayloadBytes,
            stress.KeySpace,
            stress.SetPercent,
            stress.OperationsPerLease,
            stress.BurnConnectionsTarget);

        var stats = new Stats();
        var started = Stopwatch.StartNew();

        using var tickerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var logTask = LogLoopAsync(stats, started, stress.LogEvery, tickerCts.Token);

        var workers = Enumerable.Range(0, Math.Max(1, stress.Workers))
            .Select(i => WorkerLoopAsync(i, mode, workload, Math.Max(1, stress.OperationsPerLease), deadline, operationTimeout, limiter, muxKeys, factory, modePool, modeExecutor, stats, ct))
            .ToArray();

        try
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            tickerCts.Cancel();
            try { await logTask.ConfigureAwait(false); } catch { }
            if (limiter is not null) await limiter.DisposeAsync().ConfigureAwait(false);
        }

        started.Stop();
        logger.LogInformation(
            "Redis stress finished in {Elapsed}. Ops={Ops} Ok={Ok} Fail={Fail} Timeouts={Timeouts} Burned={Burned}",
            started.Elapsed,
            stats.Ops,
            stats.Ok,
            stats.Fail,
            stats.Timeouts,
            stats.Burned);

        lifetime.StopApplication();
    }

    private async Task LogLoopAsync(Stats stats, Stopwatch started, TimeSpan every, CancellationToken ct)
    {
        if (every <= TimeSpan.Zero) return;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(every, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }

            var elapsed = Math.Max(0.001, started.Elapsed.TotalSeconds);
            var ops = Volatile.Read(ref stats.Ops);
            var ok = Volatile.Read(ref stats.Ok);
            var fail = Volatile.Read(ref stats.Fail);
            var timeouts = Volatile.Read(ref stats.Timeouts);
            var rate = ops / elapsed;

            logger.LogInformation("Redis stress: Elapsed={Elapsed} Ops={Ops} Rate={Rate:F0}/s Ok={Ok} Fail={Fail} Timeouts={Timeouts}",
                started.Elapsed, ops, rate, ok, fail, timeouts);
        }
    }

    private async Task WorkerLoopAsync(
        int workerId,
        string mode,
        string workload,
        int opsPerLease,
        DateTimeOffset? deadline,
        TimeSpan operationTimeout,
        TokenBucketPacer? limiter,
        string[]? muxKeys,
        IRedisConnectionFactory factory,
        IRedisConnectionPool? pool,
        IRedisCommandExecutor? executor,
        Stats stats,
        CancellationToken ct)
    {
        var o = redisOptions.CurrentValue;
        var burnTarget = stressOptions.Value.BurnConnectionsTarget;
        var burnLogEvery = Math.Max(1, stressOptions.Value.BurnLogEvery);
        var payloadBytes = Math.Max(0, stressOptions.Value.PayloadBytes);
        var keySpace = Math.Max(1, stressOptions.Value.KeySpace);
        var keyPrefix = stressOptions.Value.KeyPrefix ?? "vapecache:stress:";
        var setPercent = Math.Clamp(stressOptions.Value.SetPercent, 0, 100);
        var payloadTtl = stressOptions.Value.PayloadTtl;

        var payload = payloadBytes == 0 ? Array.Empty<byte>() : new byte[payloadBytes];
        if (payload.Length > 0) RandomNumberGenerator.Fill(payload);

        while (!ct.IsCancellationRequested)
        {
            if (deadline is not null && DateTimeOffset.UtcNow >= deadline.Value)
                return;

            if (mode == "burn")
            {
                if (burnTarget > 0 && Volatile.Read(ref stats.Burned) >= burnTarget)
                    return;

                if (burnTarget > 0)
                {
                    var inFlight = Interlocked.Increment(ref stats.BurnInFlight);
                    var burned = Volatile.Read(ref stats.Burned);
                    if (burned + inFlight > burnTarget)
                    {
                        Interlocked.Decrement(ref stats.BurnInFlight);
                        return;
                    }
                }

                var created = await factory.CreateAsync(ct).ConfigureAwait(false);
                if (!created.IsSuccess)
                {
                    created.IfFail(ex =>
                    {
                        Interlocked.Increment(ref stats.Fail);
                        if (ex is TimeoutException) Interlocked.Increment(ref stats.Timeouts);
                    });

                    if (burnTarget > 0)
                        Interlocked.Decrement(ref stats.BurnInFlight);
                    continue;
                }

                await created.Match(
                    async conn =>
                    {
                        await using var _ = conn.ConfigureAwait(false);
                        await EnsureSessionAsync(conn, o, ct).ConfigureAwait(false);
                        await DoPingAsync(conn, stats, operationTimeout, ct).ConfigureAwait(false);
                    },
                    _ => Task.CompletedTask).ConfigureAwait(false);

                if (burnTarget > 0)
                    Interlocked.Decrement(ref stats.BurnInFlight);

                var burnedNow = Interlocked.Increment(ref stats.Burned);
                if (burnTarget > 0 && (burnedNow % burnLogEvery) == 0)
                    logger.LogInformation("Redis burn progress: Burned={Burned}/{Target}", burnedNow, burnTarget);

                if (burnTarget > 0 && burnedNow >= burnTarget)
                    return;

                continue;
            }

            if (mode == "factory")
            {
                if (deadline is not null && DateTimeOffset.UtcNow >= deadline.Value)
                    return;

                var created = await factory.CreateAsync(ct).ConfigureAwait(false);
                if (!created.IsSuccess)
                {
                    created.IfFail(ex =>
                    {
                        Interlocked.Increment(ref stats.Fail);
                        if (ex is TimeoutException) Interlocked.Increment(ref stats.Timeouts);
                    });
                    continue;
                }

                await created.Match(
                    async conn =>
                    {
                        await using var _ = conn.ConfigureAwait(false);
                        await EnsureSessionAsync(conn, o, ct).ConfigureAwait(false);
                        await DoPingAsync(conn, stats, operationTimeout, ct).ConfigureAwait(false);
                    },
                    _ => Task.CompletedTask).ConfigureAwait(false);

                continue;
            }

            if (mode == "mux")
            {
                if (executor is null) throw new InvalidOperationException("Mux mode requires IRedisCommandExecutor.");

                var key = muxKeys is not null
                    ? muxKeys[Random.Shared.Next(0, muxKeys.Length)]
                    : keyPrefix + (Random.Shared.Next(0, keySpace).ToString());

                using var opCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                if (operationTimeout > TimeSpan.Zero)
                    opCts.CancelAfter(operationTimeout);

                try
                {
                    if (limiter is not null)
                        await limiter.WaitAsync(opCts.Token).ConfigureAwait(false);

                    if (workload == "payload")
                    {
                        if (Random.Shared.Next(0, 100) < setPercent)
                        {
                            var ok = await executor.SetAsync(key, payload, payloadTtl, opCts.Token).ConfigureAwait(false);
                            Interlocked.Increment(ref stats.Ops);
                            if (ok) Interlocked.Increment(ref stats.Ok);
                            else Interlocked.Increment(ref stats.Fail);
                        }
                        else
                        {
                            var got = await executor.GetAsync(key, opCts.Token).ConfigureAwait(false);
                            Interlocked.Increment(ref stats.Ops);
                            Interlocked.Increment(ref stats.Ok);
                            _ = got;
                        }
                    }
                    else
                    {
                        // ping-equivalent: GET on a random key (cheap read)
                        _ = await executor.GetAsync(key, opCts.Token).ConfigureAwait(false);
                        Interlocked.Increment(ref stats.Ops);
                        Interlocked.Increment(ref stats.Ok);
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    Interlocked.Increment(ref stats.Ops);
                    Interlocked.Increment(ref stats.Fail);
                    Interlocked.Increment(ref stats.Timeouts);
                }
                catch (TimeoutException)
                {
                    Interlocked.Increment(ref stats.Ops);
                    Interlocked.Increment(ref stats.Fail);
                    Interlocked.Increment(ref stats.Timeouts);
                }
                catch
                {
                    Interlocked.Increment(ref stats.Ops);
                    Interlocked.Increment(ref stats.Fail);
                }

                continue;
            }

            if (pool is null)
                throw new InvalidOperationException("Pool mode requires IRedisConnectionPool.");

            if (deadline is not null && DateTimeOffset.UtcNow >= deadline.Value)
                return;

            var leased = await pool.RentAsync(ct).ConfigureAwait(false);
            if (!leased.IsSuccess)
            {
                leased.IfFail(ex =>
                {
                    Interlocked.Increment(ref stats.Fail);
                    if (ex is TimeoutException) Interlocked.Increment(ref stats.Timeouts);
                });
                continue;
            }

            await leased.Match(
                async lease =>
                {
                    await using var _ = lease.ConfigureAwait(false);
                    await EnsureSessionAsync(lease.Connection, o, ct).ConfigureAwait(false);

                    for (int i = 0; i < opsPerLease && !ct.IsCancellationRequested; i++)
                    {
                        if (deadline is not null && DateTimeOffset.UtcNow >= deadline.Value)
                            break;

                        await DoPingAsync(lease.Connection, stats, operationTimeout, ct).ConfigureAwait(false);
                    }
                },
                _ => Task.CompletedTask).ConfigureAwait(false);
        }
    }

    private static async Task EnsureSessionAsync(IRedisConnection conn, RedisConnectionOptions options, CancellationToken ct)
    {
        // No-op: authentication/SELECT are performed once in RedisConnectionFactory.
        await Task.CompletedTask;
    }

    private static async Task DoPingAsync(IRedisConnection conn, Stats stats, TimeSpan operationTimeout, CancellationToken stoppingToken)
    {
        Interlocked.Increment(ref stats.Ops);

        using var opCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        if (operationTimeout > TimeSpan.Zero)
            opCts.CancelAfter(operationTimeout);

        var ct = opCts.Token;

        try
        {
            var cmd = RedisResp.BuildCommand("PING");
            var sent = await conn.SendAsync(cmd, ct).ConfigureAwait(false);
            if (!sent.IsSuccess)
            {
                Interlocked.Increment(ref stats.Fail);
                sent.IfFail(ex =>
                {
                    if (ex is TimeoutException) Interlocked.Increment(ref stats.Timeouts);
                    if (ex is OperationCanceledException && !stoppingToken.IsCancellationRequested)
                        Interlocked.Increment(ref stats.Timeouts);
                });
                return;
            }

            var line = await RedisResp.ReadLineAsync(conn, ct).ConfigureAwait(false);
            if (line == "+PONG")
            {
                Interlocked.Increment(ref stats.Ok);
                return;
            }

            Interlocked.Increment(ref stats.Fail);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            Interlocked.Increment(ref stats.Timeouts);
            Interlocked.Increment(ref stats.Fail);
        }
        catch (TimeoutException)
        {
            Interlocked.Increment(ref stats.Timeouts);
            Interlocked.Increment(ref stats.Fail);
        }
        catch
        {
            Interlocked.Increment(ref stats.Fail);
        }
    }

    private static async Task SendAndExpectOkAsync(IRedisConnection conn, ReadOnlyMemory<byte> cmd, CancellationToken ct)
    {
        var sent = await conn.SendAsync(cmd, ct).ConfigureAwait(false);
        if (!sent.IsSuccess)
            throw sent.Match(_ => new Exception("Send failed."), ex => ex);

        var line = await RedisResp.ReadLineAsync(conn, ct).ConfigureAwait(false);
        if (line != "+OK")
            throw new InvalidOperationException($"Unexpected response: {line}");
    }

    private async Task PreloadMuxKeysAsync(
        IRedisCommandExecutor executor,
        string[] keys,
        int payloadBytes,
        TimeSpan ttl,
        int parallelism,
        CancellationToken ct)
    {
        var payload = payloadBytes == 0 ? Array.Empty<byte>() : new byte[payloadBytes];
        if (payload.Length > 0) RandomNumberGenerator.Fill(payload);

        var index = -1;
        var workers = new Task[Math.Max(1, parallelism)];
        for (var w = 0; w < workers.Length; w++)
        {
            workers[w] = Task.Run(async () =>
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    var i = Interlocked.Increment(ref index);
                    if (i >= keys.Length) return;
                    await executor.SetAsync(keys[i], payload, ttl, ct).ConfigureAwait(false);
                }
            }, ct);
        }

        await Task.WhenAll(workers).ConfigureAwait(false);
    }

    private sealed class Stats
    {
        public long Ops;
        public long Ok;
        public long Fail;
        public long Timeouts;
        public long Burned;
        public long BurnInFlight;
    }
}
