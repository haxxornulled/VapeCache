using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;
using VapeCache.Abstractions.Caching;
using VapeCache.Reconciliation;
using VapeCache.Tests.Infrastructure;

namespace VapeCache.Tests.Caching;

public sealed class RedisReconciliationServiceTests
{
    [Fact]
    public async Task ReconcileAsync_UsesRemainingTtl()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2025, 12, 31, 0, 0, 0, TimeSpan.Zero));
        var executor = new FakeExecutor();
        var options = new TestOptionsMonitor<RedisReconciliationOptions>(new RedisReconciliationOptions
        {
            MaxOperationAge = TimeSpan.FromMinutes(5),
            MaxRunDuration = TimeSpan.FromSeconds(30),
            BatchSize = 16,
            MaxOperationsPerRun = 0,
            InitialBackoff = TimeSpan.Zero,
            MaxBackoff = TimeSpan.Zero,
            BackoffMultiplier = 1.0
        });
        var store = new InMemoryReconciliationStore();
        var service = new RedisReconciliationService(executor, options, NullLogger<RedisReconciliationService>.Instance, time, store);

        service.TrackWrite("k", new byte[] { 1, 2, 3 }, TimeSpan.FromSeconds(10));
        time.Advance(TimeSpan.FromSeconds(4));

        await service.ReconcileAsync();

        Assert.Single(executor.SetCalls);
        Assert.Equal("k", executor.SetCalls[0].Key);
        Assert.Equal(TimeSpan.FromSeconds(6), executor.SetCalls[0].Ttl);
        Assert.Equal(0, service.PendingOperations);
    }

    [Fact]
    public async Task ReconcileAsync_SkipsExpiredWrite()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2025, 12, 31, 0, 0, 0, TimeSpan.Zero));
        var executor = new FakeExecutor();
        var options = new TestOptionsMonitor<RedisReconciliationOptions>(new RedisReconciliationOptions
        {
            MaxOperationAge = TimeSpan.FromMinutes(5),
            MaxRunDuration = TimeSpan.FromSeconds(30),
            BatchSize = 16,
            MaxOperationsPerRun = 0,
            InitialBackoff = TimeSpan.Zero,
            MaxBackoff = TimeSpan.Zero,
            BackoffMultiplier = 1.0
        });
        var store = new InMemoryReconciliationStore();
        var service = new RedisReconciliationService(executor, options, NullLogger<RedisReconciliationService>.Instance, time, store);

        service.TrackWrite("k", new byte[] { 1 }, TimeSpan.FromSeconds(1));
        time.Advance(TimeSpan.FromSeconds(2));

        await service.ReconcileAsync();

        Assert.Empty(executor.SetCalls);
        Assert.Equal(0, service.PendingOperations);
    }

    [Fact]
    public async Task ReconcileAsync_SkipsStaleOperation()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2025, 12, 31, 0, 0, 0, TimeSpan.Zero));
        var executor = new FakeExecutor();
        var options = new TestOptionsMonitor<RedisReconciliationOptions>(new RedisReconciliationOptions
        {
            MaxOperationAge = TimeSpan.FromSeconds(1),
            MaxRunDuration = TimeSpan.FromSeconds(30),
            BatchSize = 16,
            MaxOperationsPerRun = 0,
            InitialBackoff = TimeSpan.Zero,
            MaxBackoff = TimeSpan.Zero,
            BackoffMultiplier = 1.0
        });
        var store = new InMemoryReconciliationStore();
        var service = new RedisReconciliationService(executor, options, NullLogger<RedisReconciliationService>.Instance, time, store);

        service.TrackWrite("k", new byte[] { 9 }, null);
        time.Advance(TimeSpan.FromSeconds(2));

        await service.ReconcileAsync();

        Assert.Empty(executor.SetCalls);
        Assert.Equal(0, service.PendingOperations);
    }

    [Fact]
    public void TrackWrite_RespectsMaxPendingOperations()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2025, 12, 31, 0, 0, 0, TimeSpan.Zero));
        var executor = new FakeExecutor();
        var options = new TestOptionsMonitor<RedisReconciliationOptions>(new RedisReconciliationOptions
        {
            MaxPendingOperations = 1,
            MaxOperationAge = TimeSpan.FromMinutes(5),
            MaxRunDuration = TimeSpan.FromSeconds(30),
            BatchSize = 16,
            MaxOperationsPerRun = 0,
            InitialBackoff = TimeSpan.Zero,
            MaxBackoff = TimeSpan.Zero,
            BackoffMultiplier = 1.0
        });
        var store = new InMemoryReconciliationStore();
        var service = new RedisReconciliationService(executor, options, NullLogger<RedisReconciliationService>.Instance, time, store);

        service.TrackWrite("k1", new byte[] { 1 }, null);
        service.TrackWrite("k2", new byte[] { 2 }, null);

        Assert.Equal(1, service.PendingOperations);
    }

    [Fact]
    public async Task ReconcileAsync_RetainsFailedOperation()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2025, 12, 31, 0, 0, 0, TimeSpan.Zero));
        var executor = new FakeExecutor { ThrowOnSet = true };
        var options = new TestOptionsMonitor<RedisReconciliationOptions>(new RedisReconciliationOptions
        {
            MaxOperationAge = TimeSpan.FromMinutes(5),
            MaxRunDuration = TimeSpan.FromSeconds(30),
            BatchSize = 16,
            MaxOperationsPerRun = 0,
            InitialBackoff = TimeSpan.Zero,
            MaxBackoff = TimeSpan.Zero,
            BackoffMultiplier = 1.0
        });
        var store = new InMemoryReconciliationStore();
        var service = new RedisReconciliationService(executor, options, NullLogger<RedisReconciliationService>.Instance, time, store);

        service.TrackWrite("k", new byte[] { 1 }, null);

        await service.ReconcileAsync();

        Assert.Equal(1, service.PendingOperations);
    }

    [Fact]
    public async Task TrackWrite_Reconciles_WhenPendingEstimateInitializationFails()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2025, 12, 31, 0, 0, 0, TimeSpan.Zero));
        var executor = new FakeExecutor();
        var options = new TestOptionsMonitor<RedisReconciliationOptions>(new RedisReconciliationOptions
        {
            MaxPendingOperations = 1,
            MaxOperationAge = TimeSpan.FromMinutes(5),
            MaxRunDuration = TimeSpan.FromSeconds(30),
            BatchSize = 16,
            MaxOperationsPerRun = 0,
            InitialBackoff = TimeSpan.Zero,
            MaxBackoff = TimeSpan.Zero,
            BackoffMultiplier = 1.0
        });
        var store = new CountFailingStore();
        var service = new RedisReconciliationService(executor, options, NullLogger<RedisReconciliationService>.Instance, time, store);

        var exception = Record.Exception(() => service.TrackWrite("k", new byte[] { 1, 2, 3 }, null));

        Assert.Null(exception);

        await service.ReconcileAsync();

        var call = Assert.Single(executor.SetCalls);
        Assert.Equal("k", call.Key);
        Assert.Equal(new byte[] { 1, 2, 3 }, call.Value);
    }

    [Fact]
    public async Task TrackDelete_Reconciles_WhenPendingEstimateInitializationFails()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2025, 12, 31, 0, 0, 0, TimeSpan.Zero));
        var executor = new FakeExecutor();
        var options = new TestOptionsMonitor<RedisReconciliationOptions>(new RedisReconciliationOptions
        {
            MaxPendingOperations = 1,
            MaxOperationAge = TimeSpan.FromMinutes(5),
            MaxRunDuration = TimeSpan.FromSeconds(30),
            BatchSize = 16,
            MaxOperationsPerRun = 0,
            InitialBackoff = TimeSpan.Zero,
            MaxBackoff = TimeSpan.Zero,
            BackoffMultiplier = 1.0
        });
        var store = new CountFailingStore();
        var service = new RedisReconciliationService(executor, options, NullLogger<RedisReconciliationService>.Instance, time, store);

        var exception = Record.Exception(() => service.TrackDelete("k"));

        Assert.Null(exception);

        await service.ReconcileAsync();

        Assert.Equal(["k"], executor.DeleteCalls);
    }

    [Fact]
    public async Task TrackWrite_DoesNotOvercount_WhenUpdatingExistingKey()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2025, 12, 31, 0, 0, 0, TimeSpan.Zero));
        var executor = new FakeExecutor();
        var options = new TestOptionsMonitor<RedisReconciliationOptions>(new RedisReconciliationOptions
        {
            MaxPendingOperations = 2,
            MaxOperationAge = TimeSpan.FromMinutes(5),
            MaxRunDuration = TimeSpan.FromSeconds(30),
            BatchSize = 16,
            MaxOperationsPerRun = 0,
            InitialBackoff = TimeSpan.Zero,
            MaxBackoff = TimeSpan.Zero,
            BackoffMultiplier = 1.0
        });
        var store = new InMemoryReconciliationStore();
        var service = new RedisReconciliationService(executor, options, NullLogger<RedisReconciliationService>.Instance, time, store);

        service.TrackWrite("k1", new byte[] { 1 }, null);
        service.TrackWrite("k1", new byte[] { 2 }, null);
        service.TrackWrite("k2", new byte[] { 3 }, null);

        await service.ReconcileAsync();

        Assert.Equal(2, executor.SetCalls.Count);
        Assert.Contains(executor.SetCalls, c => c.Key == "k1" && c.Value.AsSpan().SequenceEqual(new byte[] { 2 }));
        Assert.Contains(executor.SetCalls, c => c.Key == "k2" && c.Value.AsSpan().SequenceEqual(new byte[] { 3 }));
    }

    [Fact]
    public async Task TrackWrite_DoesNotBlock_OnSlowStorePersistence()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2025, 12, 31, 0, 0, 0, TimeSpan.Zero));
        var executor = new FakeExecutor();
        var options = new TestOptionsMonitor<RedisReconciliationOptions>(new RedisReconciliationOptions
        {
            MaxPendingOperations = 10,
            MaxOperationAge = TimeSpan.FromMinutes(5),
            MaxRunDuration = TimeSpan.FromSeconds(30),
            BatchSize = 16,
            MaxOperationsPerRun = 0,
            InitialBackoff = TimeSpan.Zero,
            MaxBackoff = TimeSpan.Zero,
            BackoffMultiplier = 1.0
        });
        var store = new SlowUpsertStore(TimeSpan.FromMilliseconds(250));
        var service = new RedisReconciliationService(executor, options, NullLogger<RedisReconciliationService>.Instance, time, store);

        var sw = Stopwatch.StartNew();
        service.TrackWrite("k", new byte[] { 1, 2, 3 }, null);
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(100), $"TrackWrite blocked for {sw.Elapsed.TotalMilliseconds} ms");

        await service.ReconcileAsync();
        Assert.Single(executor.SetCalls);
        Assert.Equal("k", executor.SetCalls[0].Key);
    }

    [Fact]
    public async Task Reaper_ReconcilesPersistedOperations_WhenPendingEstimateIsUnknown()
    {
        var executor = new FakeExecutor();
        var options = new TestOptionsMonitor<RedisReconciliationOptions>(new RedisReconciliationOptions
        {
            Enabled = true,
            MaxOperationAge = TimeSpan.FromMinutes(5),
            MaxRunDuration = TimeSpan.FromSeconds(30),
            BatchSize = 16,
            MaxOperationsPerRun = 0,
            InitialBackoff = TimeSpan.Zero,
            MaxBackoff = TimeSpan.Zero,
            BackoffMultiplier = 1.0
        });

        var store = new InMemoryReconciliationStore();
        var now = DateTimeOffset.UtcNow;
        await store.TryUpsertWriteAsync("persisted", new byte[] { 7 }, now, now.AddMinutes(1), CancellationToken.None);

        var service = new RedisReconciliationService(executor, options, NullLogger<RedisReconciliationService>.Instance, TimeProvider.System, store);
        var reaperOptions = new TestOptionsMonitor<RedisReconciliationReaperOptions>(new RedisReconciliationReaperOptions
        {
            Enabled = true,
            InitialDelay = TimeSpan.Zero,
            Interval = TimeSpan.FromMilliseconds(50)
        });
        var reaper = new RedisReconciliationReaper(service, reaperOptions, NullLogger<RedisReconciliationReaper>.Instance, TimeProvider.System);

        await reaper.StartAsync(CancellationToken.None);
        try
        {
            var sw = Stopwatch.StartNew();
            while (executor.SetCalls.Count == 0 && sw.Elapsed < TimeSpan.FromSeconds(2))
                await Task.Delay(25);
        }
        finally
        {
            await reaper.StopAsync(CancellationToken.None);
        }

        Assert.Single(executor.SetCalls);
        Assert.Equal("persisted", executor.SetCalls[0].Key);
    }

    [Fact]
    public async Task Reaper_DoesNotRun_WhenDisabled()
    {
        var executor = new FakeExecutor();
        var options = new TestOptionsMonitor<RedisReconciliationOptions>(new RedisReconciliationOptions
        {
            Enabled = true,
            MaxOperationAge = TimeSpan.FromMinutes(5),
            MaxRunDuration = TimeSpan.FromSeconds(30),
            BatchSize = 16,
            MaxOperationsPerRun = 0,
            InitialBackoff = TimeSpan.Zero,
            MaxBackoff = TimeSpan.Zero,
            BackoffMultiplier = 1.0
        });

        var store = new InMemoryReconciliationStore();
        var now = DateTimeOffset.UtcNow;
        await store.TryUpsertWriteAsync("disabled", new byte[] { 9 }, now, now.AddMinutes(1), CancellationToken.None);

        var service = new RedisReconciliationService(executor, options, NullLogger<RedisReconciliationService>.Instance, TimeProvider.System, store);
        var reaperOptions = new TestOptionsMonitor<RedisReconciliationReaperOptions>(new RedisReconciliationReaperOptions
        {
            Enabled = false,
            InitialDelay = TimeSpan.Zero,
            Interval = TimeSpan.FromMilliseconds(10)
        });
        var reaper = new RedisReconciliationReaper(service, reaperOptions, NullLogger<RedisReconciliationReaper>.Instance, TimeProvider.System);

        await reaper.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(150);
        }
        finally
        {
            await reaper.StopAsync(CancellationToken.None);
        }

        Assert.Empty(executor.SetCalls);
    }

    [Fact]
    public async Task FlushAsync_ClearsStore()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2025, 12, 31, 0, 0, 0, TimeSpan.Zero));
        var executor = new FakeExecutor();
        var options = new TestOptionsMonitor<RedisReconciliationOptions>(new RedisReconciliationOptions
        {
            MaxOperationAge = TimeSpan.FromMinutes(5),
            MaxRunDuration = TimeSpan.FromSeconds(30),
            BatchSize = 16,
            MaxOperationsPerRun = 0,
            InitialBackoff = TimeSpan.Zero,
            MaxBackoff = TimeSpan.Zero,
            BackoffMultiplier = 1.0
        });
        var store = new InMemoryReconciliationStore();
        var service = new RedisReconciliationService(executor, options, NullLogger<RedisReconciliationService>.Instance, time, store);

        service.TrackWrite("k", new byte[] { 1 }, null);
        await service.FlushAsync();

        Assert.Equal(0, service.PendingOperations);
    }

    private sealed class FakeExecutor : IRedisReconciliationExecutor
    {
        public bool ThrowOnSet { get; set; }
        public bool ThrowOnDelete { get; set; }
        public List<(string Key, byte[] Value, TimeSpan? Ttl)> SetCalls { get; } = new();
        public List<string> DeleteCalls { get; } = new();

        public ValueTask<bool> SetAsync(string key, ReadOnlyMemory<byte> value, TimeSpan? ttl, CancellationToken ct)
        {
            if (ThrowOnSet)
                throw new InvalidOperationException("set failed");

            SetCalls.Add((key, value.ToArray(), ttl));
            return ValueTask.FromResult(true);
        }

        public ValueTask<bool> DeleteAsync(string key, CancellationToken ct)
        {
            if (ThrowOnDelete)
                throw new InvalidOperationException("delete failed");

            DeleteCalls.Add(key);
            return ValueTask.FromResult(true);
        }
    }

    private sealed class CountFailingStore : IRedisReconciliationStore
    {
        private readonly InMemoryReconciliationStore _inner = new();

        public ValueTask<int> CountAsync(CancellationToken ct)
            => ValueTask.FromException<int>(new InvalidOperationException("count failed"));

        public ValueTask<bool> TryUpsertWriteAsync(string key, ReadOnlyMemory<byte> value, DateTimeOffset trackedAt, DateTimeOffset? expiresAt, CancellationToken ct)
            => _inner.TryUpsertWriteAsync(key, value, trackedAt, expiresAt, ct);

        public ValueTask<bool> TryUpsertDeleteAsync(string key, DateTimeOffset trackedAt, CancellationToken ct)
            => _inner.TryUpsertDeleteAsync(key, trackedAt, ct);

        public ValueTask<IReadOnlyList<TrackedOperation>> SnapshotAsync(int maxOperations, CancellationToken ct)
            => _inner.SnapshotAsync(maxOperations, ct);

        public ValueTask RemoveAsync(IReadOnlyList<string> keys, CancellationToken ct)
            => _inner.RemoveAsync(keys, ct);

        public ValueTask ClearAsync(CancellationToken ct)
            => _inner.ClearAsync(ct);
    }

    private sealed class SlowUpsertStore(TimeSpan delay) : IRedisReconciliationStore
    {
        private readonly InMemoryReconciliationStore _inner = new();

        public ValueTask<int> CountAsync(CancellationToken ct)
            => _inner.CountAsync(ct);

        public async ValueTask<bool> TryUpsertWriteAsync(string key, ReadOnlyMemory<byte> value, DateTimeOffset trackedAt, DateTimeOffset? expiresAt, CancellationToken ct)
        {
            await Task.Delay(delay, ct);
            return await _inner.TryUpsertWriteAsync(key, value, trackedAt, expiresAt, ct);
        }

        public async ValueTask<bool> TryUpsertDeleteAsync(string key, DateTimeOffset trackedAt, CancellationToken ct)
        {
            await Task.Delay(delay, ct);
            return await _inner.TryUpsertDeleteAsync(key, trackedAt, ct);
        }

        public ValueTask<IReadOnlyList<TrackedOperation>> SnapshotAsync(int maxOperations, CancellationToken ct)
            => _inner.SnapshotAsync(maxOperations, ct);

        public ValueTask RemoveAsync(IReadOnlyList<string> keys, CancellationToken ct)
            => _inner.RemoveAsync(keys, ct);

        public ValueTask ClearAsync(CancellationToken ct)
            => _inner.ClearAsync(ct);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;
        private DateTimeOffset _utcNow;

        public ManualTimeProvider(DateTimeOffset initialUtc)
        {
            _utcNow = initialUtc;
        }

        public override long TimestampFrequency => 1_000_000_000;

        public override long GetTimestamp() => Volatile.Read(ref _timestamp);

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan by)
        {
            _utcNow = _utcNow.Add(by);
            var delta = (long)(by.TotalSeconds * TimestampFrequency);
            Interlocked.Add(ref _timestamp, delta);
        }
    }
}
