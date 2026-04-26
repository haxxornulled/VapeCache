using System.Globalization;
using System.Text;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Diagnostics;

namespace VapeCache.StressHost;

public sealed partial class RuntimeStressCoordinator : IHostedLifecycleService, IDisposable
{
    private readonly IVapeCache _cache;
    private readonly IDistributedCache _distributedCache;
    private readonly ICacheStats _cacheStats;
    private readonly ICacheOriginStats _originStats;
    private readonly ICacheBackendState _backendState;
    private readonly IRedisFailoverController _failoverController;
    private readonly IOptionsMonitor<RuntimeStressHostOptions> _options;
    private readonly ILogger<RuntimeStressCoordinator> _logger;
    private readonly object _gate = new();

    private CancellationTokenSource? _runCts;
    private Task? _runTask;
    private RuntimeStressRunStatus _status = RuntimeStressRunStatus.Idle();

    public RuntimeStressCoordinator(
        IVapeCache cache,
        IDistributedCache distributedCache,
        ICacheStats cacheStats,
        ICacheOriginStats originStats,
        ICacheBackendState backendState,
        IRedisFailoverController failoverController,
        IOptionsMonitor<RuntimeStressHostOptions> options,
        ILogger<RuntimeStressCoordinator> logger)
    {
        _cache = cache;
        _distributedCache = distributedCache;
        _cacheStats = cacheStats;
        _originStats = originStats;
        _backendState = backendState;
        _failoverController = failoverController;
        _options = options;
        _logger = logger;
    }

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StartedAsync(CancellationToken cancellationToken)
    {
        if (!_options.CurrentValue.AutoStart)
            return;

        _ = await StartRunAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task StoppingAsync(CancellationToken cancellationToken) => StopRunAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public RuntimeStressRunStatus GetStatus()
    {
        lock (_gate)
        {
            return _status with
            {
                CacheStats = _cacheStats.Snapshot,
                OriginStats = _originStats.Snapshot,
                EffectiveBackend = _backendState.EffectiveBackend,
                IsForcedOpen = _failoverController.IsForcedOpen,
                ForcedOpenReason = _failoverController.Reason
            };
        }
    }

    public Task<RuntimeStressRunStatus> StartRunAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_runTask is not null && !_runTask.IsCompleted)
                return Task.FromResult(GetStatus());

            var options = Normalize(_options.CurrentValue);
            _runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _status = RuntimeStressRunStatus.Starting(options);
            _runTask = Task.Run(() => RunAsync(options, _runCts.Token), CancellationToken.None);
            return Task.FromResult(GetStatus());
        }
    }

    public async Task<RuntimeStressRunStatus> StopRunAsync(CancellationToken ct = default)
    {
        Task? runTask;
        lock (_gate)
        {
            runTask = _runTask;
            _runCts?.Cancel();
        }

        if (runTask is not null)
        {
            try
            {
                await runTask.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        return GetStatus();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _runCts?.Cancel();
            _runCts?.Dispose();
            _runCts = null;
            _runTask = null;
        }
    }

    private async Task RunAsync(RuntimeStressHostOptions options, CancellationToken ct)
    {
        UpdateStatus(status => status with
        {
            State = "running",
            StartedUtc = DateTimeOffset.UtcNow,
            Scenario = options.Scenario,
            Config = options
        });

        var nativeKeys = new CacheKey<string>[options.Keyspace];
        var interopKeys = new string[options.Keyspace];
        for (var slot = 0; slot < options.Keyspace; slot++)
        {
            var suffix = slot.ToString(CultureInfo.InvariantCulture);
            nativeKeys[slot] = CacheKey<string>.From($"vapecache:stress:live:{options.Scenario}:native:{suffix}");
            interopKeys[slot] = $"vapecache:stress:live:{options.Scenario}:interop:{suffix}";
        }

        var ttl = TimeSpan.FromSeconds(30);
        var distributedOptions = new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = ttl
        };

        using var durationCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        durationCts.CancelAfter(TimeSpan.FromSeconds(options.DurationSeconds));
        var runToken = durationCts.Token;

        var cycleTask = Task.Run(async () =>
        {
            if (options.ForceOpenCycles <= 0)
                return;

            await Task.Delay(options.ForceOpenAfterMs, runToken).ConfigureAwait(false);
            for (var cycle = 0; cycle < options.ForceOpenCycles && !runToken.IsCancellationRequested; cycle++)
            {
                _failoverController.ForceOpen($"stress-host-cycle-{cycle.ToString(CultureInfo.InvariantCulture)}");
                UpdateStatus(status => status with { CompletedFailoverCycles = cycle });
                await Task.Delay(options.ForceOpenHoldMs, runToken).ConfigureAwait(false);
                _failoverController.ClearForcedOpen();

                if (cycle < options.ForceOpenCycles - 1)
                    await Task.Delay(options.ForceOpenAfterMs, runToken).ConfigureAwait(false);

                UpdateStatus(status => status with { CompletedFailoverCycles = cycle + 1 });
            }
        }, runToken);

        var stampedeTask = options.StampedeEnabled
            ? Task.Run(() => RunStampedePressureAsync(options, runToken), runToken)
            : Task.CompletedTask;

        var workers = Enumerable.Range(0, options.Workers)
            .Select(workerId => Task.Run(async () =>
            {
                var random = new Random(unchecked((Environment.TickCount * 397) ^ workerId));
                var nativePayload = $"native:{workerId.ToString(CultureInfo.InvariantCulture)}";
                var interopPayload = Encoding.UTF8.GetBytes($"interop:{workerId.ToString(CultureInfo.InvariantCulture)}");

                while (!runToken.IsCancellationRequested)
                {
                    var slot = random.Next(0, options.Keyspace);
                    try
                    {
                        if ((workerId & 1) == 0)
                        {
                            var key = nativeKeys[slot];
                            await _cache.SetAsync(key, nativePayload, new CacheEntryOptions(ttl), runToken).ConfigureAwait(false);
                            var got = await _cache.GetAsync(key, runToken).ConfigureAwait(false);
                            if (!string.Equals(nativePayload, got, StringComparison.Ordinal))
                                RecordFailure();

                            if ((slot & 7) == 0)
                                _ = await _cache.RemoveAsync(key, runToken).ConfigureAwait(false);
                        }
                        else
                        {
                            var key = interopKeys[slot];
                            await _distributedCache.SetAsync(key, interopPayload, distributedOptions, runToken).ConfigureAwait(false);
                            var got = await _distributedCache.GetAsync(key, runToken).ConfigureAwait(false);
                            if (got is null || !got.AsSpan().SequenceEqual(interopPayload))
                                RecordFailure();

                            if ((slot & 3) == 0)
                                await _distributedCache.RefreshAsync(key, runToken).ConfigureAwait(false);
                            if ((slot & 7) == 0)
                                await _distributedCache.RemoveAsync(key, runToken).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException) when (runToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        RecordFailure();
                        LogRuntimeStressWorkerFailure(_logger, ex);
                    }
                    finally
                    {
                        RecordOperation();
                    }
                }
            }, runToken))
            .ToArray();

        try
        {
            await Task.WhenAll(workers.Append(cycleTask).Append(stampedeTask)).ConfigureAwait(false);
            UpdateStatus(status => status with
            {
                State = "completed",
                EndedUtc = DateTimeOffset.UtcNow,
                EffectiveBackend = _backendState.EffectiveBackend,
                CacheStats = _cacheStats.Snapshot,
                OriginStats = _originStats.Snapshot,
                IsForcedOpen = _failoverController.IsForcedOpen,
                ForcedOpenReason = _failoverController.Reason
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || durationCts.IsCancellationRequested)
        {
            UpdateStatus(status => status with
            {
                State = "stopped",
                EndedUtc = DateTimeOffset.UtcNow,
                EffectiveBackend = _backendState.EffectiveBackend,
                CacheStats = _cacheStats.Snapshot,
                OriginStats = _originStats.Snapshot,
                IsForcedOpen = _failoverController.IsForcedOpen,
                ForcedOpenReason = _failoverController.Reason
            });
        }
        catch (Exception ex)
        {
            LogRuntimeStressHostFailed(_logger, ex);
            UpdateStatus(status => status with
            {
                State = "faulted",
                EndedUtc = DateTimeOffset.UtcNow,
                LastError = ex.Message,
                EffectiveBackend = _backendState.EffectiveBackend,
                CacheStats = _cacheStats.Snapshot,
                OriginStats = _originStats.Snapshot,
                IsForcedOpen = _failoverController.IsForcedOpen,
                ForcedOpenReason = _failoverController.Reason
            });
        }
        finally
        {
            _failoverController.ClearForcedOpen();
            lock (_gate)
            {
                _runCts?.Dispose();
                _runCts = null;
                _runTask = null;
            }
        }
    }

    private async Task RunStampedePressureAsync(RuntimeStressHostOptions options, CancellationToken ct)
    {
        var stampedeOptions = new CacheEntryOptions(TimeSpan.FromSeconds(30));
        var hotKey = CacheKey<string>.From($"vapecache:stress:live:{options.Scenario}:stampede:hot");
        var wave = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(options.StampedeWaveIntervalMs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await _cache.RemoveAsync(hotKey, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogRuntimeStressWorkerFailure(_logger, ex);
            }

            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var factoryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var currentWave = wave++;
            var contenders = Enumerable.Range(0, Math.Max(2, options.StampedeWorkers))
                .Select(workerId => Task.Run(async () =>
                {
                    try
                    {
                        await gate.Task.ConfigureAwait(false);
                        await _cache.GetOrCreateAsync(
                                hotKey,
                                async token =>
                                {
                                    factoryEntered.TrySetResult();
                                    await Task.Delay(options.StampedeFactoryDelayMs, token).ConfigureAwait(false);
                                    return $"stampede:{currentWave}:{workerId}";
                                },
                                stampedeOptions,
                                ct)
                            .ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        // Expected pressure signal.
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                    }
                    catch (Exception ex)
                    {
                        LogRuntimeStressWorkerFailure(_logger, ex);
                    }
                }, ct))
                .ToArray();

            gate.TrySetResult();

            try
            {
                await factoryEntered.Task.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await Task.WhenAll(contenders).ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    private void RecordOperation()
    {
        UpdateStatus(status => status with { TotalOperations = status.TotalOperations + 1 });
    }

    private void RecordFailure()
    {
        UpdateStatus(status => status with { TotalFailures = status.TotalFailures + 1 });
    }

    private void UpdateStatus(Func<RuntimeStressRunStatus, RuntimeStressRunStatus> update)
    {
        lock (_gate)
        {
            _status = update(_status);
        }
    }

    private static RuntimeStressHostOptions Normalize(RuntimeStressHostOptions options)
    {
        return new RuntimeStressHostOptions
        {
            AutoStart = options.AutoStart,
            Scenario = string.IsNullOrWhiteSpace(options.Scenario) ? "soak" : options.Scenario,
            Workers = Math.Max(4, options.Workers),
            Keyspace = Math.Max(16, options.Keyspace),
            DurationSeconds = Math.Max(5, options.DurationSeconds),
            ForceOpenAfterMs = Math.Max(250, options.ForceOpenAfterMs),
            ForceOpenHoldMs = Math.Max(500, options.ForceOpenHoldMs),
            ForceOpenCycles = Math.Max(0, options.ForceOpenCycles),
            SampleIntervalMs = Math.Max(50, options.SampleIntervalMs)
        };
    }

    [LoggerMessage(
        EventId = 31000,
        Level = LogLevel.Warning,
        Message = "Runtime stress worker failure.")]
    private static partial void LogRuntimeStressWorkerFailure(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 31001,
        Level = LogLevel.Error,
        Message = "Runtime stress host run failed.")]
    private static partial void LogRuntimeStressHostFailed(ILogger logger, Exception exception);
}

public sealed record RuntimeStressRunStatus(
    string State,
    string Scenario,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? EndedUtc,
    long TotalOperations,
    long TotalFailures,
    int CompletedFailoverCycles,
    string? LastError,
    RuntimeStressHostOptions Config,
    CacheStatsSnapshot CacheStats,
    CacheOriginStatsSnapshot OriginStats,
    BackendType EffectiveBackend,
    bool IsForcedOpen,
    string? ForcedOpenReason)
{
    public static RuntimeStressRunStatus Idle()
        => new(
            State: "idle",
            Scenario: "soak",
            StartedUtc: null,
            EndedUtc: null,
            TotalOperations: 0,
            TotalFailures: 0,
            CompletedFailoverCycles: 0,
            LastError: null,
            Config: new RuntimeStressHostOptions(),
            CacheStats: default,
            OriginStats: default,
            EffectiveBackend: BackendType.Redis,
            IsForcedOpen: false,
            ForcedOpenReason: null);

    public static RuntimeStressRunStatus Starting(RuntimeStressHostOptions options)
        => Idle() with
        {
            State = "starting",
            Scenario = options.Scenario,
            Config = options
        };
}
