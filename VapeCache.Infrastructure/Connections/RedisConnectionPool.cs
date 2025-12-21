using System.Threading.Channels;
using System.Net.Sockets;
using LanguageExt.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Connections;

namespace VapeCache.Infrastructure.Connections;

internal sealed class RedisConnectionPool : IRedisConnectionPool, IRedisConnectionPoolReaper
{
    private readonly Channel<PooledConnection> _idle;
    private readonly SemaphoreSlim _connectionSlots;
    private readonly IRedisConnectionFactory _factory;
    private readonly IOptionsMonitor<RedisConnectionOptions> _options;
    private readonly ILogger<RedisConnectionPool> _logger;
    private readonly List<PooledConnection> _reaperKept = new(64);

    private int _disposed;
    private long _created;
    private long _returned;
    private long _disposedConnections;
    private long _idleCount;

    public RedisConnectionPool(
        IRedisConnectionFactory factory,
        IOptionsMonitor<RedisConnectionOptions> options,
        ILogger<RedisConnectionPool> logger)
    {
        _factory = factory;
        _options = options;
        _logger = logger;

        var o = options.CurrentValue;
        var maxConnections = Math.Max(1, o.MaxConnections);
        var maxIdle = Math.Clamp(o.MaxIdle, 0, maxConnections);
        _connectionSlots = new SemaphoreSlim(maxConnections, maxConnections);

        _idle = Channel.CreateBounded<PooledConnection>(new BoundedChannelOptions(maxIdle)
        {
            SingleReader = false,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });

        _ = WarmAsync();
    }

    public async ValueTask<Result<IRedisConnectionLease>> RentAsync(CancellationToken ct)
    {
        RedisTelemetry.PoolAcquires.Add(1);

        if (Volatile.Read(ref _disposed) == 1)
            return new Result<IRedisConnectionLease>(new ObjectDisposedException(nameof(RedisConnectionPool)));

        var o = _options.CurrentValue;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(o.AcquireTimeout);

        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            while (true)
            {
                if (_idle.Reader.TryRead(out var pc))
                {
                    Interlocked.Decrement(ref _idleCount);

                    if (TryGetDropReason(pc, o, out var dropReason))
                    {
                        TrackDrop(dropReason);
                        TryLogDrop("borrow", dropReason, pc);
                        await DisposeAndReleaseAsync(pc).ConfigureAwait(false);
                        continue;
                    }

                    if (ShouldValidateOnBorrow(pc, o))
                    {
                        RedisTelemetry.PoolValidations.Add(1);
                        var ok = await TryValidateAsync(pc, o, cts.Token).ConfigureAwait(false);
                        if (!ok)
                        {
                            TrackDrop("validate-failed");
                            TryLogDrop("borrow", "validate-failed", pc);
                            await DisposeAndReleaseAsync(pc).ConfigureAwait(false);
                            continue;
                        }
                    }

                    sw.Stop();
                    RedisTelemetry.PoolWaitMs.Record(sw.Elapsed.TotalMilliseconds);
                    TryLogLease("reuse", pc);
                    return new Result<IRedisConnectionLease>(new Lease(this, pc));
                }

                if (_connectionSlots.Wait(0))
                {
                    var created = await _factory.CreateAsync(cts.Token).ConfigureAwait(false);
                    if (created.IsSuccess)
                    {
                        sw.Stop();
                        RedisTelemetry.PoolWaitMs.Record(sw.Elapsed.TotalMilliseconds);

                        return created.Match(
                            succ =>
                            {
                                var pc = new PooledConnection(succ);
                                Interlocked.Increment(ref _created);
                                TryLogLease("create", pc);
                                return new Result<IRedisConnectionLease>(new Lease(this, pc));
                            },
                            fail =>
                            {
                                _connectionSlots.Release();
                                return new Result<IRedisConnectionLease>(fail);
                            });
                    }

                    created.IfFail(_ => { });
                    _connectionSlots.Release();
                }

                // Wait for someone to return a connection (until timeout)
                while (await _idle.Reader.WaitToReadAsync(cts.Token).ConfigureAwait(false))
                {
                    if (_idle.Reader.TryRead(out var waited))
                    {
                        Interlocked.Decrement(ref _idleCount);

                        if (TryGetDropReason(waited, o, out var dropReason))
                        {
                            TrackDrop(dropReason);
                            TryLogDrop("borrow", dropReason, waited);
                            await DisposeAndReleaseAsync(waited).ConfigureAwait(false);
                            break;
                        }

                        if (ShouldValidateOnBorrow(waited, o))
                        {
                            RedisTelemetry.PoolValidations.Add(1);
                            var ok = await TryValidateAsync(waited, o, cts.Token).ConfigureAwait(false);
                            if (!ok)
                            {
                                TrackDrop("validate-failed");
                                TryLogDrop("borrow", "validate-failed", waited);
                                await DisposeAndReleaseAsync(waited).ConfigureAwait(false);
                                break;
                            }
                        }

                        sw.Stop();
                        RedisTelemetry.PoolWaitMs.Record(sw.Elapsed.TotalMilliseconds);
                        TryLogLease("wait-reuse", waited);
                        return new Result<IRedisConnectionLease>(new Lease(this, waited));
                    }
                }

                sw.Stop();
                RedisTelemetry.PoolWaitMs.Record(sw.Elapsed.TotalMilliseconds);
                RedisTelemetry.PoolTimeouts.Add(1);
                return new Result<IRedisConnectionLease>(new TimeoutException("Acquire timed out."));
            }
        }
        catch (OperationCanceledException oce) when (!ct.IsCancellationRequested)
        {
            sw.Stop();
            RedisTelemetry.PoolWaitMs.Record(sw.Elapsed.TotalMilliseconds);
            RedisTelemetry.PoolTimeouts.Add(1);
            return new Result<IRedisConnectionLease>(new TimeoutException("Acquire timed out.", oce));
        }
        catch (Exception ex)
        {
            sw.Stop();
            RedisTelemetry.PoolWaitMs.Record(sw.Elapsed.TotalMilliseconds);
            return new Result<IRedisConnectionLease>(ex);
        }
    }

    private static bool ShouldValidateOnBorrow(PooledConnection pc, RedisConnectionOptions o)
    {
        if (o.ValidateAfterIdle <= TimeSpan.Zero) return false;
        return pc.IdleFor >= o.ValidateAfterIdle;
    }

    private static bool TryGetDropReason(PooledConnection pc, RedisConnectionOptions o, out string reason)
    {
        if (pc.IsFaulted)
        {
            reason = "faulted";
            return true;
        }

        if (o.IdleTimeout > TimeSpan.Zero && pc.IdleFor >= o.IdleTimeout)
        {
            reason = "idle-timeout";
            return true;
        }

        if (o.MaxConnectionLifetime > TimeSpan.Zero && pc.Age >= o.MaxConnectionLifetime)
        {
            reason = "max-lifetime";
            return true;
        }

        reason = "";
        return false;
    }

    private static async Task<bool> TryValidateAsync(PooledConnection pc, RedisConnectionOptions o, CancellationToken ct)
    {
        using var validateCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (o.ValidateTimeout > TimeSpan.Zero)
            validateCts.CancelAfter(o.ValidateTimeout);

        try
        {
            await pc.Inner.Stream.WriteAsync(RedisRespProtocol.PingCommand, validateCts.Token).ConfigureAwait(false);
            await RedisRespProtocol.ExpectPongAsync(pc.Inner.Stream, validateCts.Token).ConfigureAwait(false);
            pc.MarkUsedNow();
            return true;
        }
        catch
        {
            RedisTelemetry.PoolValidationFailures.Add(1);
            return false;
        }
    }

    private async Task WarmAsync()
    {
        try
        {
            var o = _options.CurrentValue;
            var warmTarget = Math.Clamp(o.Warm, 0, Math.Max(0, Math.Min(o.MaxIdle, o.MaxConnections)));

            if (warmTarget <= 0) return;
            _logger.LogInformation("Redis pool warming: Warm={Warm} MaxConnections={MaxConnections} MaxIdle={MaxIdle}", warmTarget, o.MaxConnections, o.MaxIdle);

            await MaintainWarmAsync(o, warmTarget, CancellationToken.None).ConfigureAwait(false);

            _logger.LogInformation("Redis pool warm complete: Created={Created} Idle={Idle} Disposed={Disposed}", _created, _idleCount, _disposedConnections);
        }
        catch { }
    }

    private async Task MaintainWarmAsync(RedisConnectionOptions o, int warmTarget, CancellationToken ct)
    {
        warmTarget = Math.Min(warmTarget, Math.Max(0, o.MaxIdle));

        while (Volatile.Read(ref _disposed) == 0 &&
               Volatile.Read(ref _idleCount) < warmTarget &&
               _connectionSlots.Wait(0))
        {
            var created = await _factory.CreateAsync(ct).ConfigureAwait(false);
            if (!created.IsSuccess)
            {
                _connectionSlots.Release();
                return;
            }

            await created.Match(
                async succ =>
                {
                    var pc = new PooledConnection(succ);
                    Interlocked.Increment(ref _created);

                    if (!_idle.Writer.TryWrite(pc))
                    {
                        await DisposeAndReleaseAsync(pc).ConfigureAwait(false);
                        return;
                    }

                    Interlocked.Increment(ref _idleCount);
                    Interlocked.Increment(ref _returned);
                    TryLogReturn("warm", pc);
                },
                _ =>
                {
                    _connectionSlots.Release();
                    return Task.CompletedTask;
                }).ConfigureAwait(false);
        }
    }

    private async ValueTask ReturnAsync(PooledConnection pc)
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            TrackDrop("disposed");
            TryLogDrop("return", "disposed", pc);
            await DisposeAndReleaseAsync(pc).ConfigureAwait(false);
            return;
        }

        var o = _options.CurrentValue;
        if (TryGetDropReason(pc, o, out var dropReason))
        {
            TrackDrop(dropReason);
            TryLogDrop("return", dropReason, pc);
            await DisposeAndReleaseAsync(pc).ConfigureAwait(false);
            return;
        }

        pc.MarkUsedNow();

        if (_idle.Writer.TryWrite(pc))
        {
            Interlocked.Increment(ref _idleCount);
            Interlocked.Increment(ref _returned);
            TryLogReturn("return", pc);
            return;
        }

        TrackDrop("idle-full");
        TryLogDrop("return", "idle-full", pc);
        await DisposeAndReleaseAsync(pc).ConfigureAwait(false);
    }

    private async Task DisposeAndReleaseAsync(PooledConnection pc)
    {
        try { await pc.DisposeAsync().ConfigureAwait(false); } catch { }
        _connectionSlots.Release();
        Interlocked.Increment(ref _disposedConnections);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        _idle.Writer.TryComplete();

        while (_idle.Reader.TryRead(out var pc))
        {
            Interlocked.Decrement(ref _idleCount);
            await DisposeAndReleaseAsync(pc).ConfigureAwait(false);
        }

        await _factory.DisposeAsync().ConfigureAwait(false);
        _logger.LogInformation("Redis pool disposed: Created={Created} Returned={Returned} Idle={Idle} Disposed={Disposed}", _created, _returned, _idleCount, _disposedConnections);
    }

    public async Task RunReaperAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && Volatile.Read(ref _disposed) == 0)
        {
            var o = _options.CurrentValue;
            var period = o.ReaperPeriod;

            try
            {
                if (period <= TimeSpan.Zero)
                    await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                else
                    await Task.Delay(period, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }

            try
            {
                if (period > TimeSpan.Zero)
                    await ReapOnceAsync(o, ct).ConfigureAwait(false);
            }
            catch { }
        }
    }

    private async Task ReapOnceAsync(RedisConnectionOptions o, CancellationToken ct)
    {
        var maxIdle = Math.Clamp(o.MaxIdle, 0, Math.Max(1, o.MaxConnections));
        var warmTarget = Math.Clamp(o.Warm, 0, Math.Max(0, Math.Min(maxIdle, o.MaxConnections)));

        var kept = _reaperKept;
        kept.Clear();
        var disposed = 0;
        Dictionary<string, int>? disposedByReason = null;

        while (_idle.Reader.TryRead(out var pc))
        {
            Interlocked.Decrement(ref _idleCount);

            if (TryGetDropReason(pc, o, out var dropReason))
            {
                TrackDrop(dropReason);
                TryLogDrop("reap", dropReason, pc);
                disposedByReason ??= new Dictionary<string, int>(StringComparer.Ordinal);
                disposedByReason[dropReason] = disposedByReason.TryGetValue(dropReason, out var v) ? v + 1 : 1;
                await DisposeAndReleaseAsync(pc).ConfigureAwait(false);
                disposed++;
                continue;
            }

            kept.Add(pc);
        }

        foreach (var pc in kept)
        {
            if (!_idle.Writer.TryWrite(pc))
            {
                await DisposeAndReleaseAsync(pc).ConfigureAwait(false);
                disposed++;
                continue;
            }
            Interlocked.Increment(ref _idleCount);
        }

        if (disposed > 0)
        {
            RedisTelemetry.PoolReaps.Add(disposed);
            _logger.LogInformation("Redis pool reaped: Disposed={Disposed} Idle={Idle} Created={Created}", disposed, _idleCount, _created);
            if (disposedByReason is not null)
                _logger.LogInformation("Redis pool reap reasons: {Reasons}", FormatReasons(disposedByReason));
        }

        if (warmTarget > 0 && Volatile.Read(ref _idleCount) < warmTarget)
            await MaintainWarmAsync(o, warmTarget, ct).ConfigureAwait(false);

        // Cap idle to MaxIdle (in case config changed downward)
        while (Volatile.Read(ref _idleCount) > maxIdle && _idle.Reader.TryRead(out var extra))
        {
            Interlocked.Decrement(ref _idleCount);
            TrackDrop("max-idle");
            TryLogDrop("reap", "max-idle", extra);
            await DisposeAndReleaseAsync(extra).ConfigureAwait(false);
        }
    }

    private static string FormatReasons(Dictionary<string, int> reasons)
    {
        var sb = new System.Text.StringBuilder(reasons.Count * 16);
        var first = true;
        foreach (var kvp in reasons)
        {
            if (!first) sb.Append(", ");
            first = false;
            sb.Append(kvp.Key).Append('=').Append(kvp.Value);
        }
        return sb.ToString();
    }

    private sealed class Lease : IRedisConnectionLease
    {
        private RedisConnectionPool? _pool;
        private PooledConnection? _conn;

        public Lease(RedisConnectionPool pool, PooledConnection conn)
        {
            _pool = pool;
            _conn = conn;
        }

        public IRedisConnection Connection => _conn ?? throw new ObjectDisposedException(nameof(Lease));

        public async ValueTask DisposeAsync()
        {
            var pool = Interlocked.Exchange(ref _pool, null);
            var conn = Interlocked.Exchange(ref _conn, null);
            if (pool is not null && conn is not null)
                await pool.ReturnAsync(conn).ConfigureAwait(false);
        }
    }

    private sealed class PooledConnection : IRedisConnection
    {
        private int _faulted;
        private long _lastUsedTicks;
        private readonly long _createdTicks;
        private string? _lastErrorType;
        private string? _lastErrorMessage;

        public PooledConnection(IRedisConnection inner)
        {
            Inner = inner;
            _createdTicks = StopwatchTicksNow();
            _lastUsedTicks = _createdTicks;
        }

        public IRedisConnection Inner { get; }
        public bool IsFaulted => Volatile.Read(ref _faulted) == 1;
        public string? LastErrorType => Volatile.Read(ref _lastErrorType);
        public string? LastErrorMessage => Volatile.Read(ref _lastErrorMessage);

        public Socket Socket => Inner.Socket;
        public Stream Stream => Inner.Stream;

        public TimeSpan IdleFor => StopwatchTicksToTimeSpan(StopwatchTicksNow() - Volatile.Read(ref _lastUsedTicks));
        public TimeSpan Age => StopwatchTicksToTimeSpan(StopwatchTicksNow() - _createdTicks);

        public void MarkUsedNow() => Volatile.Write(ref _lastUsedTicks, StopwatchTicksNow());

        public async ValueTask<Result<LanguageExt.Unit>> SendAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct)
        {
            var r = await Inner.SendAsync(buffer, ct).ConfigureAwait(false);
            if (r.IsSuccess) MarkUsedNow();
            else r.IfFail(StoreFault);
            return r;
        }

        public async ValueTask<Result<int>> ReceiveAsync(Memory<byte> buffer, CancellationToken ct)
        {
            var r = await Inner.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (r.IsSuccess) MarkUsedNow();
            else r.IfFail(StoreFault);
            return r;
        }

        public ValueTask DisposeAsync() => Inner.DisposeAsync();

        private void StoreFault(Exception ex)
        {
            Interlocked.Exchange(ref _faulted, 1);
            try
            {
                Volatile.Write(ref _lastErrorType, ex.GetType().FullName);
                var msg = ex.Message;
                if (msg.Length > 300) msg = msg[..300];
                Volatile.Write(ref _lastErrorMessage, msg);
            }
            catch
            {
            }
        }

        private static long StopwatchTicksNow() => System.Diagnostics.Stopwatch.GetTimestamp();

        private static TimeSpan StopwatchTicksToTimeSpan(long ticks)
        {
            if (ticks <= 0) return TimeSpan.Zero;
            return TimeSpan.FromSeconds((double)ticks / System.Diagnostics.Stopwatch.Frequency);
        }
    }

    private void TrackDrop(string reason)
    {
        RedisTelemetry.PoolDrops.Add(1, new KeyValuePair<string, object?>("reason", reason));
    }

    private void TryLogDrop(string stage, string reason, PooledConnection conn)
    {
        try
        {
            if (conn.Inner is RedisConnection rc)
            {
                _logger.LogInformation(
                    "Redis conn drop ({Stage}): Reason={Reason} Id={Id} IdleMs={IdleMs} AgeMs={AgeMs} Faulted={Faulted} LastErrorType={LastErrorType} LastError={LastError} Idle={Idle}",
                    stage,
                    reason,
                    rc.Id,
                    (long)conn.IdleFor.TotalMilliseconds,
                    (long)conn.Age.TotalMilliseconds,
                    conn.IsFaulted,
                    conn.LastErrorType,
                    conn.LastErrorMessage,
                    Volatile.Read(ref _idleCount));
            }
            else
            {
                _logger.LogInformation("Redis conn drop ({Stage}): Reason={Reason}", stage, reason);
            }
        }
        catch { }
    }

    private void TryLogLease(string kind, PooledConnection conn)
    {
        try
        {
            var created = Volatile.Read(ref _created);
            var returned = Volatile.Read(ref _returned);

            if (kind is "reuse" or "wait-reuse")
            {
                if (returned > 10 && (returned % 1000) != 0) return;
            }

            if (conn.Inner is RedisConnection rc)
            {
                _logger.LogInformation(
                    "Redis lease ({Kind}): Id={Id} RemoteEndPoint={RemoteEndPoint} Created={Created} Returned={Returned} IdleMs={IdleMs}",
                    kind,
                    rc.Id,
                    rc.Socket.RemoteEndPoint?.ToString() ?? "?",
                    created,
                    returned,
                    (long)conn.IdleFor.TotalMilliseconds);
            }
            else
            {
                _logger.LogInformation("Redis lease ({Kind})", kind);
            }
        }
        catch { }
    }

    private void TryLogReturn(string kind, PooledConnection conn)
    {
        try
        {
            var created = Volatile.Read(ref _created);
            var returned = Volatile.Read(ref _returned);

            if (kind == "return")
            {
                if (returned > 10 && (returned % 1000) != 0) return;
            }

            if (conn.Inner is RedisConnection rc)
            {
                _logger.LogInformation(
                    "Redis pool add ({Kind}): Id={Id} Created={Created} Returned={Returned} Idle={Idle}",
                    kind,
                    rc.Id,
                    created,
                    returned,
                    Volatile.Read(ref _idleCount));
            }
            else
            {
                _logger.LogInformation("Redis pool add ({Kind})", kind);
            }
        }
        catch { }
    }
}
