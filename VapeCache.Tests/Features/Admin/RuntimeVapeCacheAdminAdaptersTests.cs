using System.Text.Json;
using Moq;
using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Connections;
using VapeCache.Abstractions.Diagnostics;
using VapeCache.UI.Features.Admin;

namespace VapeCache.Tests.Features.Admin;

public sealed class RuntimeVapeCacheAdminAdaptersTests
{
    [Fact]
    public void StatsSnapshotProvider_UsesFreshSharedSnapshot()
    {
        var shared = CreateSharedSnapshot(
            timestampUtc: DateTimeOffset.UtcNow,
            backend: BackendType.Redis,
            reads: 128,
            writes: 64,
            hits: 96,
            misses: 32,
            fallbackToMemory: 12,
            breakerOpened: 4,
            stampedeKeyRejected: 3,
            stampedeLockWaitTimeout: 9,
            stampedeFailureBackoffRejected: 7,
            breakerEnabled: true,
            breakerOpen: true,
            breakerConsecutiveFailures: 5,
            breakerOpenRemaining: TimeSpan.FromSeconds(18),
            breakerForcedOpen: true,
            breakerReason: "forced test",
            autoscaler: CreateAutoscaler(),
            lanes: [CreateLane(0, healthy: true), CreateLane(1, healthy: false)],
            spill: CreateSpill(),
            originStats: new CacheOriginStatsSnapshot(11, 22, 9, 2, 117, 42, 87, 30));

        var provider = CreateStatsProvider(shared, localBackend: BackendType.InMemory);

        var snapshot = provider.GetSnapshot();

        Assert.Equal(shared.TimestampUtc, snapshot.TimestampUtc);
        Assert.Equal(shared.Backend, snapshot.Backend);
        Assert.Equal(shared.Reads, snapshot.Reads);
        Assert.Equal(shared.Writes, snapshot.Writes);
        Assert.Equal(shared.HitRate, snapshot.HitRate);
        Assert.Equal(shared.Reads, snapshot.Stats.GetCalls);
        Assert.Equal(shared.FallbackToMemory, snapshot.Stats.FallbackToMemory);
        Assert.Equal(shared.RedisBreakerOpened, snapshot.Stats.RedisBreakerOpened);
        Assert.Equal(shared.StampedeLockWaitTimeout, snapshot.Stats.StampedeLockWaitTimeout);
        Assert.Equal(2, snapshot.Lanes.Count);
        Assert.Equal(1, snapshot.HealthyLaneCount);
    }

    [Fact]
    public void StatsSnapshotProvider_FallsBackWhenSharedSnapshotIsStale()
    {
        var shared = CreateSharedSnapshot(
            timestampUtc: DateTimeOffset.UtcNow.AddSeconds(-30),
            backend: BackendType.Redis,
            reads: 300,
            writes: 150,
            hits: 240,
            misses: 60,
            fallbackToMemory: 40,
            breakerOpened: 2,
            stampedeKeyRejected: 1,
            stampedeLockWaitTimeout: 2,
            stampedeFailureBackoffRejected: 3,
            breakerEnabled: true,
            breakerOpen: true,
            breakerConsecutiveFailures: 8,
            breakerOpenRemaining: TimeSpan.FromSeconds(3),
            breakerForcedOpen: true,
            breakerReason: "stale",
            autoscaler: CreateAutoscaler(),
            lanes: [CreateLane(0, healthy: true)],
            spill: CreateSpill(),
            originStats: new CacheOriginStatsSnapshot(5, 6, 7, 8, 9, 10, 11, 12));

        var provider = CreateStatsProvider(shared, localBackend: BackendType.InMemory);

        var snapshot = provider.GetSnapshot();

        Assert.Equal(BackendType.InMemory, snapshot.Backend);
        Assert.Equal(15, snapshot.Reads);
        Assert.Equal(3, snapshot.Writes);
        Assert.Equal(0.8d, snapshot.HitRate);
        Assert.Equal(0, snapshot.HealthyLaneCount);
    }

    [Fact]
    public void BreakerStatusProvider_UsesSharedSnapshot()
    {
        var shared = CreateSharedSnapshot(
            timestampUtc: DateTimeOffset.UtcNow,
            backend: BackendType.Redis,
            reads: 1,
            writes: 1,
            hits: 1,
            misses: 0,
            fallbackToMemory: 0,
            breakerOpened: 13,
            stampedeKeyRejected: 0,
            stampedeLockWaitTimeout: 0,
            stampedeFailureBackoffRejected: 0,
            breakerEnabled: true,
            breakerOpen: true,
            breakerConsecutiveFailures: 13,
            breakerOpenRemaining: TimeSpan.FromSeconds(44),
            breakerForcedOpen: true,
            breakerReason: "shared breaker",
            autoscaler: null,
            lanes: Array.Empty<RedisMuxLaneSnapshot>(),
            spill: null,
            originStats: new CacheOriginStatsSnapshot(0, 0, 0, 0, 0, 0, 0, 0));

        var provider = CreateBreakerProvider(shared);

        var breaker = provider.GetStatus();

        Assert.True(breaker.Enabled);
        Assert.True(breaker.IsOpen);
        Assert.Equal(13, breaker.ConsecutiveFailures);
        Assert.Equal(TimeSpan.FromSeconds(44), breaker.OpenRemaining);
        Assert.True(breaker.IsForcedOpen);
        Assert.Equal("shared breaker", breaker.Reason);
        Assert.False(breaker.HalfOpenProbeInFlight);
    }

    [Fact]
    public void AutoscalerStatusProvider_UsesSharedSnapshot()
    {
        var autoscaler = CreateAutoscaler();
        var shared = CreateSharedSnapshot(
            timestampUtc: DateTimeOffset.UtcNow,
            backend: BackendType.Redis,
            reads: 1,
            writes: 1,
            hits: 1,
            misses: 0,
            fallbackToMemory: 0,
            breakerOpened: 0,
            stampedeKeyRejected: 0,
            stampedeLockWaitTimeout: 0,
            stampedeFailureBackoffRejected: 0,
            breakerEnabled: true,
            breakerOpen: false,
            breakerConsecutiveFailures: 0,
            breakerOpenRemaining: null,
            breakerForcedOpen: false,
            breakerReason: null,
            autoscaler: autoscaler,
            lanes: Array.Empty<RedisMuxLaneSnapshot>(),
            spill: null,
            originStats: new CacheOriginStatsSnapshot(0, 0, 0, 0, 0, 0, 0, 0));

        var provider = CreateAutoscalerProvider(shared);

        var status = provider.GetStatus();

        Assert.NotNull(status);
        Assert.Equal(autoscaler.CurrentConnections, status!.CurrentConnections);
        Assert.Equal(autoscaler.CurrentReadLanes, status.CurrentReadLanes);
        Assert.Equal(autoscaler.PressureTier, status.PressureTier);
    }

    [Fact]
    public void SpillDiagnosticsProvider_UsesSharedSnapshot()
    {
        var spill = CreateSpill();
        var shared = CreateSharedSnapshot(
            timestampUtc: DateTimeOffset.UtcNow,
            backend: BackendType.Redis,
            reads: 1,
            writes: 1,
            hits: 1,
            misses: 0,
            fallbackToMemory: 0,
            breakerOpened: 0,
            stampedeKeyRejected: 0,
            stampedeLockWaitTimeout: 0,
            stampedeFailureBackoffRejected: 0,
            breakerEnabled: true,
            breakerOpen: false,
            breakerConsecutiveFailures: 0,
            breakerOpenRemaining: null,
            breakerForcedOpen: false,
            breakerReason: null,
            autoscaler: null,
            lanes: Array.Empty<RedisMuxLaneSnapshot>(),
            spill: spill,
            originStats: new CacheOriginStatsSnapshot(0, 0, 0, 0, 0, 0, 0, 0));

        var provider = CreateSpillProvider(shared);

        var status = provider.GetStatus();

        Assert.NotNull(status);
        Assert.Equal(spill.Mode, status!.Mode);
        Assert.Equal(spill.TotalSpillFiles, status.TotalSpillFiles);
        Assert.Equal(spill.ActiveShards, status.ActiveShards);
    }

    [Fact]
    public async Task AdminOrchestrator_ProjectsSharedSnapshotIntoUiModel()
    {
        var shared = CreateSharedSnapshot(
            timestampUtc: DateTimeOffset.UtcNow,
            backend: BackendType.Redis,
            reads: 512,
            writes: 128,
            hits: 480,
            misses: 32,
            fallbackToMemory: 18,
            breakerOpened: 6,
            stampedeKeyRejected: 4,
            stampedeLockWaitTimeout: 9,
            stampedeFailureBackoffRejected: 11,
            breakerEnabled: true,
            breakerOpen: true,
            breakerConsecutiveFailures: 6,
            breakerOpenRemaining: TimeSpan.FromSeconds(17),
            breakerForcedOpen: true,
            breakerReason: "shared breaker",
            autoscaler: CreateAutoscaler(),
            lanes: [CreateLane(0, healthy: true), CreateLane(1, healthy: true), CreateLane(2, healthy: false)],
            spill: CreateSpill(),
            originStats: new CacheOriginStatsSnapshot(9, 8, 7, 6, 5, 4, 3, 2));

        var orchestrator = new VapeCacheAdminOrchestrator(
            CreateStatsProvider(shared, localBackend: BackendType.InMemory),
            Mock.Of<IVapeCacheAdminInvalidationOperationsFacade>(),
            CreateAutoscalerProvider(shared),
            CreateSpillProvider(shared),
            Mock.Of<IVapeCacheAdminReconciliationStatusProvider>(x =>
                x.GetStatus() == new VapeCacheAdminReconciliationStatus(Enabled: false, PendingOperations: 0)),
            CreateBreakerProvider(shared),
            Mock.Of<IVapeCacheAdminPolicyInspectionProvider>(x => x.GetRecent(It.IsAny<int>()) == Array.Empty<CacheIntentEntry>()),
            Mock.Of<IVapeCacheAdminEventStreamFeedProvider>(x => x.GetStatus() == VapeCacheAdminEventStreamStatus.Disabled));

        var snapshot = await orchestrator.GetSnapshotAsync();

        Assert.Equal(BackendType.Redis, snapshot.Backend);
        Assert.Equal(512, snapshot.Reads);
        Assert.Equal(128, snapshot.Writes);
        Assert.Equal(0.9375d, snapshot.HitRate);
        Assert.Equal(3, snapshot.Lanes.Count);
        Assert.Equal(2, snapshot.HealthyLaneCount);
        Assert.Equal(6, snapshot.BreakerConsecutiveFailures);
        Assert.True(snapshot.BreakerOpen);
        Assert.True(snapshot.BreakerForcedOpen);
        Assert.Equal(TimeSpan.FromSeconds(17), snapshot.BreakerOpenRemaining);
        Assert.Equal("shared breaker", snapshot.BreakerReason);
        Assert.NotNull(snapshot.Autoscaler);
        Assert.NotNull(snapshot.Spill);
    }

    private static RuntimeVapeCacheAdminStatsSnapshotProvider CreateStatsProvider(
        VapeCacheSharedDashboardSnapshot? sharedSnapshot,
        BackendType localBackend)
    {
        var redis = CreateRedis(sharedSnapshot);
        var backend = new Mock<ICacheBackendState>(MockBehavior.Strict);
        backend.SetupGet(x => x.EffectiveBackend).Returns(localBackend);

        var stats = new Mock<ICacheStats>(MockBehavior.Strict);
        stats.SetupGet(x => x.Snapshot).Returns(new CacheStatsSnapshot(
            GetCalls: 15,
            Hits: 12,
            Misses: 3,
            SetCalls: 2,
            RemoveCalls: 1,
            FallbackToMemory: 1,
            RedisBreakerOpened: 0,
            StampedeKeyRejected: 0,
            StampedeLockWaitTimeout: 0,
            StampedeFailureBackoffRejected: 0));

        return new RuntimeVapeCacheAdminStatsSnapshotProvider(
            backend.Object,
            stats.Object,
            redis.Object,
            Array.Empty<IRedisMultiplexerDiagnostics>());
    }

    private static RuntimeVapeCacheAdminBreakerStatusProvider CreateBreakerProvider(VapeCacheSharedDashboardSnapshot? sharedSnapshot)
    {
        var redis = CreateRedis(sharedSnapshot);
        var breaker = Mock.Of<IRedisCircuitBreakerState>(x =>
            x.Enabled == false &&
            x.IsOpen == false &&
            x.ConsecutiveFailures == 0 &&
            x.OpenRemaining == null &&
            x.HalfOpenProbeInFlight == false);
        var failover = Mock.Of<IRedisFailoverController>(x =>
            x.IsForcedOpen == false &&
            x.Reason == "local");

        return new RuntimeVapeCacheAdminBreakerStatusProvider(redis.Object, breaker, failover);
    }

    private static RuntimeVapeCacheAdminAutoscalerStatusProvider CreateAutoscalerProvider(VapeCacheSharedDashboardSnapshot? sharedSnapshot)
    {
        var redis = CreateRedis(sharedSnapshot);
        return new RuntimeVapeCacheAdminAutoscalerStatusProvider(redis.Object, Array.Empty<IRedisMultiplexerDiagnostics>());
    }

    private static RuntimeVapeCacheAdminSpillDiagnosticsProvider CreateSpillProvider(VapeCacheSharedDashboardSnapshot? sharedSnapshot)
    {
        var redis = CreateRedis(sharedSnapshot);
        return new RuntimeVapeCacheAdminSpillDiagnosticsProvider(redis.Object, null);
    }

    private static Mock<IRedisCommandExecutor> CreateRedis(VapeCacheSharedDashboardSnapshot? snapshot)
    {
        var redis = new Mock<IRedisCommandExecutor>(MockBehavior.Strict);
        redis.Setup(x => x.GetAsync(VapeCacheSharedDashboardSnapshotStore.RedisKey, It.IsAny<CancellationToken>()))
            .Returns((string _, CancellationToken _) =>
            {
                if (snapshot is null)
                    return ValueTask.FromResult<byte[]?>(null);

                var payload = JsonSerializer.SerializeToUtf8Bytes(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                return ValueTask.FromResult<byte[]?>(payload);
            });

        return redis;
    }

    private static VapeCacheSharedDashboardSnapshot CreateSharedSnapshot(
        DateTimeOffset timestampUtc,
        BackendType backend,
        long reads,
        long writes,
        long hits,
        long misses,
        long fallbackToMemory,
        long breakerOpened,
        long stampedeKeyRejected,
        long stampedeLockWaitTimeout,
        long stampedeFailureBackoffRejected,
        bool breakerEnabled,
        bool breakerOpen,
        int breakerConsecutiveFailures,
        TimeSpan? breakerOpenRemaining,
        bool breakerForcedOpen,
        string? breakerReason,
        RedisAutoscalerSnapshot? autoscaler,
        IReadOnlyList<RedisMuxLaneSnapshot> lanes,
        SpillStoreDiagnosticsSnapshot? spill,
        CacheOriginStatsSnapshot originStats)
    {
        return new VapeCacheSharedDashboardSnapshot(
            TimestampUtc: timestampUtc,
            Backend: backend,
            HitRate: reads <= 0 ? 0d : (double)hits / reads,
            Reads: reads,
            Writes: writes,
            Hits: hits,
            Misses: misses,
            FallbackToMemory: fallbackToMemory,
            RedisBreakerOpened: breakerOpened,
            StampedeKeyRejected: stampedeKeyRejected,
            StampedeLockWaitTimeout: stampedeLockWaitTimeout,
            StampedeFailureBackoffRejected: stampedeFailureBackoffRejected,
            BreakerEnabled: breakerEnabled,
            BreakerOpen: breakerOpen,
            BreakerConsecutiveFailures: breakerConsecutiveFailures,
            BreakerOpenRemaining: breakerOpenRemaining,
            BreakerForcedOpen: breakerForcedOpen,
            BreakerReason: breakerReason,
            Autoscaler: autoscaler,
            Lanes: lanes,
            Spill: spill,
            OriginStats: originStats);
    }

    private static RedisAutoscalerSnapshot CreateAutoscaler()
        => new(
            Enabled: true,
            CurrentConnections: 64,
            TargetConnections: 64,
            MinConnections: 16,
            MaxConnections: 64,
            CurrentReadLanes: 48,
            CurrentWriteLanes: 16,
            HighSignalCount: 3,
            AvgInflightUtilization: 0.57d,
            AvgQueueDepth: 0.1d,
            MaxQueueDepth: 4,
            TimeoutRatePerSec: 0.01d,
            RollingP95LatencyMs: 2.5d,
            RollingP99LatencyMs: 5.1d,
            UnhealthyConnections: 0,
            ReconnectFailureRatePerSec: 0d,
            ScaleEventsInCurrentMinute: 0,
            MaxScaleEventsPerMinute: 20,
            Frozen: false,
            FrozenUntilUtc: null,
            FreezeReason: null,
            LastScaleEventUtc: null,
            LastScaleDirection: null,
            LastScaleReason: null,
            SpillSignalCount: 2,
            SpillTotalFiles: 0,
            SpillActiveShards: 0,
            SpillImbalanceRatio: 0.1d,
            PressureScore: 0.2d,
            PressureTier: "normal");

    private static RedisMuxLaneSnapshot CreateLane(int laneIndex, bool healthy)
        => new(
            LaneIndex: laneIndex,
            ConnectionId: laneIndex + 1,
            Role: healthy ? "read-write" : "bulk-read-write",
            WriteQueueDepth: healthy ? 0 : 1,
            InFlight: healthy ? 1 : 2,
            MaxInFlight: 4096,
            InFlightUtilization: healthy ? 0.01d : 0.02d,
            BytesSent: 10 + laneIndex,
            BytesReceived: 20 + laneIndex,
            Operations: 100 + laneIndex,
            Failures: healthy ? 0 : 1,
            Responses: 100 + laneIndex,
            OrphanedResponses: 0,
            ResponseSequenceMismatches: 0,
            TransportResets: 0,
            Healthy: healthy);

    private static SpillStoreDiagnosticsSnapshot CreateSpill()
        => new(
            SupportsDiskSpill: true,
            SpillToDiskConfigured: true,
            Mode: "spill",
            TotalSpillFiles: 12,
            ActiveShards: 4,
            MaxFilesInShard: 5,
            AvgFilesPerActiveShard: 3d,
            ImbalanceRatio: 1.2d,
            TopShards: [new SpillShardLoad("0", 5), new SpillShardLoad("1", 4)],
            SampledAtUtc: DateTimeOffset.UtcNow);
}
