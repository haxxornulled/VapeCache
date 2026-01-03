using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Caching;
using VapeCache.Reconciliation;

namespace VapeCache.Tests.Caching;

public sealed class RedisReconciliationServiceTests
{
    [Fact]
    public async Task ReconcileAsync_UsesRemainingTtl()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2025, 12, 31, 0, 0, 0, TimeSpan.Zero));
        var executor = new FakeExecutor();
        var options = Options.Create(new RedisReconciliationOptions
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
        var options = Options.Create(new RedisReconciliationOptions
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
        var options = Options.Create(new RedisReconciliationOptions
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
        var options = Options.Create(new RedisReconciliationOptions
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
        var options = Options.Create(new RedisReconciliationOptions
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
    public async Task FlushAsync_ClearsStore()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2025, 12, 31, 0, 0, 0, TimeSpan.Zero));
        var executor = new FakeExecutor();
        var options = Options.Create(new RedisReconciliationOptions
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
