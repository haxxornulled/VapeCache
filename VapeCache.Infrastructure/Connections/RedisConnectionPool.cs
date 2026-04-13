using System.Net.Sockets;
using LanguageExt.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Connections;

namespace VapeCache.Infrastructure.Connections;

internal sealed partial class RedisConnectionPool : IRedisConnectionPool, IRedisConnectionPoolReaper
{
    private readonly IdleConnectionCache _idle;
    private readonly AsyncInFlightGate _connectionSlots;
    private readonly PoolSignal _availabilitySignal = new();
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
        var maxIdle = Math.Clamp(Math.Max(1, o.MaxIdle), 1, maxConnections);
        if (o.MaxIdle <= 0)
        {
            LogMaxIdleAdjusted(_logger, o.MaxIdle, maxIdle);
        }
        _connectionSlots = new AsyncInFlightGate(maxConnections, maxConnections);
        _idle = new IdleConnectionCache(maxIdle);

        _ = WarmAsync();
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<Result<IRedisConnectionLease>> RentAsync(CancellationToken ct)
    {
        RedisTelemetry.PoolAcquires.Add(1);

        if (Volatile.Read(ref _disposed) == 1)
            return new Result<IRedisConnectionLease>(new ObjectDisposedException(nameof(RedisConnectionPool)));

        var o = _options.CurrentValue;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        CancellationTokenSource? timeoutCts = null;

        try
        {
            while (true)
            {
                if (_idle.TryTake(out var pc))
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
                        var rentToken = GetOrCreateAcquireToken(ref timeoutCts, o.AcquireTimeout, ct);
                        var ok = await TryValidateAsync(pc, o, rentToken).ConfigureAwait(false);
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

                if (TryAcquireConnectionSlot())
                {
                    var rentToken = GetOrCreateAcquireToken(ref timeoutCts, o.AcquireTimeout, ct);
                    var created = await _factory.CreateAsync(rentToken).ConfigureAwait(false);
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
                                ReleaseConnectionSlot();
                                return new Result<IRedisConnectionLease>(fail);
                            });
                    }

                    created.IfFail(_ => { });
                    ReleaseConnectionSlot();
                    continue;
                }

                var rentWaitToken = GetOrCreateAcquireToken(ref timeoutCts, o.AcquireTimeout, ct);
                var observedVersion = _availabilitySignal.Version;
                if (Volatile.Read(ref _idleCount) > 0 || _connectionSlots.CurrentCount > 0)
                    continue;

                await _availabilitySignal.WaitAsync(observedVersion, rentWaitToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException oce) when (!ct.IsCancellationRequested && timeoutCts is not null && timeoutCts.IsCancellationRequested)
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
        finally
        {
            timeoutCts?.Dispose();
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

    private static CancellationToken GetOrCreateAcquireToken(
        ref CancellationTokenSource? timeoutCts,
        TimeSpan acquireTimeout,
        CancellationToken ct)
    {
        if (timeoutCts is null)
            timeoutCts = CreateAcquireTimeoutSource(acquireTimeout, ct);

        return timeoutCts?.Token ?? ct;
    }

    private static CancellationTokenSource? CreateAcquireTimeoutSource(TimeSpan acquireTimeout, CancellationToken ct)
    {
        if (acquireTimeout <= TimeSpan.Zero || acquireTimeout == Timeout.InfiniteTimeSpan)
        {
            if (!ct.CanBeCanceled)
                return null;

            return CancellationTokenSource.CreateLinkedTokenSource(ct);
        }

        if (!ct.CanBeCanceled)
        {
            var timeoutOnly = new CancellationTokenSource();
            timeoutOnly.CancelAfter(acquireTimeout);
            return timeoutOnly;
        }

        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(acquireTimeout);
        return linked;
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
            LogPoolWarming(_logger, warmTarget, o.MaxConnections, o.MaxIdle);

            await MaintainWarmAsync(o, warmTarget, CancellationToken.None).ConfigureAwait(false);

            LogPoolWarmComplete(_logger, _created, _idleCount, _disposedConnections);
        }
        catch (Exception ex)
        {
            // Log warm-up failure but don't crash - pool can still work with on-demand connection creation
            LogPoolWarmFailed(_logger, ex, _created, _idleCount);
        }
    }

    private async Task MaintainWarmAsync(RedisConnectionOptions o, int warmTarget, CancellationToken ct)
    {
        warmTarget = Math.Min(warmTarget, Math.Max(0, o.MaxIdle));
        const int MaxWarmupRetries = 10;
        var retryCount = 0;
        var maxTotalAttempts = Math.Max(warmTarget * 4, MaxWarmupRetries * 2);
        var totalAttempts = 0;

        while (Volatile.Read(ref _disposed) == 0 &&
               Volatile.Read(ref _idleCount) < warmTarget &&
               TryAcquireConnectionSlot())
        {
            totalAttempts++;
            if (totalAttempts > maxTotalAttempts)
            {
                LogPoolWarmAttemptsExceeded(_logger, maxTotalAttempts, warmTarget, Volatile.Read(ref _idleCount));
                ReleaseConnectionSlot();
                return;
            }

            var created = await _factory.CreateAsync(ct).ConfigureAwait(false);
            if (!created.IsSuccess)
            {
                ReleaseConnectionSlot();
                retryCount++;

                if (retryCount >= MaxWarmupRetries)
                {
                    LogPoolWarmRetriesExceeded(_logger, MaxWarmupRetries);
                    return;
                }

                var backoffMs = Math.Min(1000, 50 * (1 << (retryCount - 1)));
                await Task.Delay(backoffMs, ct).ConfigureAwait(false);
                continue;
            }

            retryCount = 0;

            await created.Match(
                async succ =>
                {
                    var pc = new PooledConnection(succ);
                    Interlocked.Increment(ref _created);

                    if (!_idle.TryAdd(pc))
                    {
                        await DisposeAndReleaseAsync(pc).ConfigureAwait(false);
                        return;
                    }

                    Interlocked.Increment(ref _idleCount);
                    Interlocked.Increment(ref _returned);
                    _availabilitySignal.Set();
                    TryLogReturn("warm", pc);
                },
                _ =>
                {
                    ReleaseConnectionSlot();
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

        if (_idle.TryAdd(pc))
        {
            Interlocked.Increment(ref _idleCount);
            Interlocked.Increment(ref _returned);
            _availabilitySignal.Set();
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
        ReleaseConnectionSlot();
        Interlocked.Increment(ref _disposedConnections);
    }

    /// <summary>
    /// Asynchronously releases resources used by the current instance.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        while (_idle.TryTake(out var pc))
        {
            Interlocked.Decrement(ref _idleCount);
            await DisposeAndReleaseAsync(pc).ConfigureAwait(false);
        }

        await _factory.DisposeAsync().ConfigureAwait(false);
        _availabilitySignal.Dispose();
        _connectionSlots.Dispose();
        LogPoolDisposed(_logger, _created, _returned, _idleCount, _disposedConnections);
    }

    /// <summary>
    /// Runs value.
    /// </summary>
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

        while (_idle.TryTake(out var pc))
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
            if (!_idle.TryAdd(pc))
            {
                await DisposeAndReleaseAsync(pc).ConfigureAwait(false);
                disposed++;
                continue;
            }

            _availabilitySignal.Set();
            Interlocked.Increment(ref _idleCount);
        }

        if (disposed > 0)
        {
            RedisTelemetry.PoolReaps.Add(disposed);
            LogPoolReaped(_logger, disposed, _idleCount, _created);
            if (disposedByReason is not null && _logger.IsEnabled(LogLevel.Information))
            {
                var reasons = FormatReasons(disposedByReason);
                LogPoolReapReasons(_logger, reasons);
            }
        }

        if (warmTarget > 0 && Volatile.Read(ref _idleCount) < warmTarget)
            await MaintainWarmAsync(o, warmTarget, ct).ConfigureAwait(false);

        // Cap idle to MaxIdle (in case config changed downward)
        while (Volatile.Read(ref _idleCount) > maxIdle && _idle.TryTake(out var extra))
        {
            Interlocked.Decrement(ref _idleCount);
            TrackDrop("max-idle");
            TryLogDrop("reap", "max-idle", extra);
            await DisposeAndReleaseAsync(extra).ConfigureAwait(false);
        }
    }

    private bool TryAcquireConnectionSlot()
        => _connectionSlots.Wait(0);

    private void ReleaseConnectionSlot()
    {
        _connectionSlots.Release();
        _availabilitySignal.Set();
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

    private sealed class IdleConnectionCache
    {
        private readonly System.Threading.Lock _gate = new();
        private readonly PooledConnection?[] _items;
        private int _count;

        public IdleConnectionCache(int capacity)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
            _items = new PooledConnection[capacity];
        }

        public bool TryAdd(PooledConnection item)
        {
            lock (_gate)
            {
                if (_count == _items.Length)
                    return false;

                _items[_count++] = item;
                return true;
            }
        }

        public bool TryTake(out PooledConnection item)
        {
            lock (_gate)
            {
                if (_count == 0)
                {
                    item = null!;
                    return false;
                }

                var next = --_count;
                item = _items[next]!;
                _items[next] = null;
                return true;
            }
        }
    }

    private sealed class PoolSignal : IDisposable
    {
        private volatile TaskCompletionSource<bool>? _waiters;
        private long _version;
        private int _disposed;

        public long Version => Volatile.Read(ref _version);

        public void Set()
        {
            if (Volatile.Read(ref _disposed) == 1)
                return;

            Interlocked.Increment(ref _version);
            var waiters = Interlocked.Exchange(ref _waiters, null);
            waiters?.TrySetResult(true);
        }

        public ValueTask WaitAsync(long observedVersion, CancellationToken ct)
        {
            if (Volatile.Read(ref _disposed) == 1 || Volatile.Read(ref _version) != observedVersion)
                return ValueTask.CompletedTask;

            while (true)
            {
                var current = _waiters;
                if (Volatile.Read(ref _disposed) == 1 || Volatile.Read(ref _version) != observedVersion)
                    return ValueTask.CompletedTask;

                if (current is null)
                {
                    var created = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var prior = Interlocked.CompareExchange(ref _waiters, created, null);
                    current = prior ?? created;
                    if (prior is not null)
                        continue;
                }

                if (Volatile.Read(ref _disposed) == 1 || Volatile.Read(ref _version) != observedVersion)
                    return ValueTask.CompletedTask;

                return new ValueTask(current.Task.WaitAsync(ct));
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
                return;

            Interlocked.Exchange(ref _waiters, null)?.TrySetCanceled();
        }
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

        /// <summary>
        /// Asynchronously releases resources used by the current instance.
        /// </summary>
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

        /// <summary>
        /// Executes value.
        /// </summary>
        public void MarkUsedNow() => Volatile.Write(ref _lastUsedTicks, StopwatchTicksNow());

        /// <summary>
        /// Executes value.
        /// </summary>
        public async ValueTask<Result<LanguageExt.Unit>> SendAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct)
        {
            var r = await Inner.SendAsync(buffer, ct).ConfigureAwait(false);
            if (r.IsSuccess) MarkUsedNow();
            else r.IfFail(StoreFault);
            return r;
        }

        /// <summary>
        /// Executes value.
        /// </summary>
        public async ValueTask<Result<int>> ReceiveAsync(Memory<byte> buffer, CancellationToken ct)
        {
            var r = await Inner.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (r.IsSuccess) MarkUsedNow();
            else r.IfFail(StoreFault);
            return r;
        }

        /// <summary>
        /// Asynchronously releases resources used by the current instance.
        /// </summary>
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

    [LoggerMessage(
        EventId = 12000,
        Level = LogLevel.Warning,
        Message = "RedisConnectionOptions.MaxIdle was {ConfiguredMaxIdle}. Using {EffectiveMaxIdle} to keep the pool operational.")]
    private static partial void LogMaxIdleAdjusted(ILogger logger, int configuredMaxIdle, int effectiveMaxIdle);

    [LoggerMessage(
        EventId = 12001,
        Level = LogLevel.Information,
        Message = "Redis pool warming: Warm={Warm} MaxConnections={MaxConnections} MaxIdle={MaxIdle}")]
    private static partial void LogPoolWarming(ILogger logger, int warm, int maxConnections, int maxIdle);

    [LoggerMessage(
        EventId = 12002,
        Level = LogLevel.Information,
        Message = "Redis pool warm complete: Created={Created} Idle={Idle} Disposed={Disposed}")]
    private static partial void LogPoolWarmComplete(ILogger logger, long created, long idle, long disposed);

    [LoggerMessage(
        EventId = 12003,
        Level = LogLevel.Warning,
        Message = "Redis pool warm-up failed, pool will create connections on-demand: Created={Created} Idle={Idle}")]
    private static partial void LogPoolWarmFailed(ILogger logger, Exception exception, long created, long idle);

    [LoggerMessage(
        EventId = 12004,
        Level = LogLevel.Warning,
        Message = "Failed to warm connection pool after {Attempts} attempts (WarmTarget={WarmTarget}, Idle={Idle}). Stopping warmup to prevent infinite loop.")]
    private static partial void LogPoolWarmAttemptsExceeded(ILogger logger, int attempts, int warmTarget, long idle);

    [LoggerMessage(
        EventId = 12005,
        Level = LogLevel.Warning,
        Message = "Failed to warm connection pool after {Retries} attempts. Redis may be unavailable. Pool will attempt to create connections on-demand.")]
    private static partial void LogPoolWarmRetriesExceeded(ILogger logger, int retries);

    [LoggerMessage(
        EventId = 12006,
        Level = LogLevel.Information,
        Message = "Redis pool disposed: Created={Created} Returned={Returned} Idle={Idle} Disposed={Disposed}")]
    private static partial void LogPoolDisposed(ILogger logger, long created, long returned, long idle, long disposed);

    [LoggerMessage(
        EventId = 12007,
        Level = LogLevel.Information,
        Message = "Redis pool reaped: Disposed={Disposed} Idle={Idle} Created={Created}")]
    private static partial void LogPoolReaped(ILogger logger, int disposed, long idle, long created);

    [LoggerMessage(
        EventId = 12008,
        Level = LogLevel.Information,
        Message = "Redis pool reap reasons: {Reasons}")]
    private static partial void LogPoolReapReasons(ILogger logger, string reasons);

    [LoggerMessage(
        EventId = 12009,
        Level = LogLevel.Information,
        Message = "Redis conn drop ({Stage}): Reason={Reason} Id={Id} IdleMs={IdleMs} AgeMs={AgeMs} Faulted={Faulted} LastErrorType={LastErrorType} LastError={LastError} Idle={Idle}")]
    private static partial void LogConnectionDropDetailed(
        ILogger logger,
        string stage,
        string reason,
        long id,
        long idleMs,
        long ageMs,
        bool faulted,
        string? lastErrorType,
        string? lastError,
        long idle);

    [LoggerMessage(
        EventId = 12010,
        Level = LogLevel.Information,
        Message = "Redis conn drop ({Stage}): Reason={Reason}")]
    private static partial void LogConnectionDrop(ILogger logger, string stage, string reason);

    [LoggerMessage(
        EventId = 12011,
        Level = LogLevel.Information,
        Message = "Redis lease ({Kind}): Id={Id} RemoteEndPoint={RemoteEndPoint} Created={Created} Returned={Returned} IdleMs={IdleMs}")]
    private static partial void LogConnectionLeaseDetailed(
        ILogger logger,
        string kind,
        long id,
        object? remoteEndPoint,
        long created,
        long returned,
        long idleMs);

    [LoggerMessage(
        EventId = 12012,
        Level = LogLevel.Information,
        Message = "Redis lease ({Kind})")]
    private static partial void LogConnectionLease(ILogger logger, string kind);

    [LoggerMessage(
        EventId = 12013,
        Level = LogLevel.Information,
        Message = "Redis pool add ({Kind}): Id={Id} Created={Created} Returned={Returned} Idle={Idle}")]
    private static partial void LogPoolAddDetailed(
        ILogger logger,
        string kind,
        long id,
        long created,
        long returned,
        long idle);

    [LoggerMessage(
        EventId = 12014,
        Level = LogLevel.Information,
        Message = "Redis pool add ({Kind})")]
    private static partial void LogPoolAdd(ILogger logger, string kind);

    private static void TrackDrop(string reason)
    {
        RedisTelemetry.PoolDrops.Add(1, new KeyValuePair<string, object?>("reason", reason));
    }

    private void TryLogDrop(string stage, string reason, PooledConnection conn)
    {
        if (!_logger.IsEnabled(LogLevel.Information))
            return;

        try
        {
            if (conn.Inner is RedisConnection rc)
            {
                var idle = Volatile.Read(ref _idleCount);
                LogConnectionDropDetailed(
                    _logger,
                    stage,
                    reason,
                    rc.Id,
                    (long)conn.IdleFor.TotalMilliseconds,
                    (long)conn.Age.TotalMilliseconds,
                    conn.IsFaulted,
                    conn.LastErrorType,
                    conn.LastErrorMessage,
                    idle);
            }
            else
            {
                LogConnectionDrop(_logger, stage, reason);
            }
        }
        catch { }
    }

    private void TryLogLease(string kind, PooledConnection conn)
    {
        if (!_logger.IsEnabled(LogLevel.Information))
            return;

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
                var remoteEndPoint = rc.Socket.RemoteEndPoint;
                LogConnectionLeaseDetailed(
                    _logger,
                    kind,
                    rc.Id,
                    remoteEndPoint,
                    created,
                    returned,
                    (long)conn.IdleFor.TotalMilliseconds);
            }
            else
            {
                LogConnectionLease(_logger, kind);
            }
        }
        catch { }
    }

    private void TryLogReturn(string kind, PooledConnection conn)
    {
        if (!_logger.IsEnabled(LogLevel.Information))
            return;

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
                var idle = Volatile.Read(ref _idleCount);
                LogPoolAddDetailed(
                    _logger,
                    kind,
                    rc.Id,
                    created,
                    returned,
                    idle);
            }
            else
            {
                LogPoolAdd(_logger, kind);
            }
        }
        catch { }
    }
}
