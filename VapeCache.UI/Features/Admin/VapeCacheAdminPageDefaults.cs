using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Connections;
using VapeCache.Abstractions.Diagnostics;

namespace VapeCache.UI.Features.Admin;

internal static class VapeCacheAdminPageDefaults
{
    public static VapeCacheAdminSnapshot EmptySnapshot { get; } = new(
        TimestampUtc: DateTimeOffset.MinValue,
        Backend: BackendType.Redis,
        Reads: 0,
        Writes: 0,
        HitRate: 0d,
        Stats: new CacheStatsSnapshot(
            GetCalls: 0,
            Hits: 0,
            Misses: 0,
            SetCalls: 0,
            RemoveCalls: 0,
            FallbackToMemory: 0,
            RedisBreakerOpened: 0,
            StampedeKeyRejected: 0,
            StampedeLockWaitTimeout: 0,
            StampedeFailureBackoffRejected: 0),
        BreakerEnabled: false,
        BreakerOpen: false,
        BreakerConsecutiveFailures: 0,
        BreakerOpenRemaining: null,
        BreakerHalfOpenProbeInFlight: false,
        BreakerForcedOpen: false,
        BreakerReason: null,
        Autoscaler: null,
        Lanes: Array.Empty<RedisMuxLaneSnapshot>(),
        HealthyLaneCount: 0,
        Spill: null,
        ReconciliationPendingOperations: 0,
        ReconciliationEnabled: false);
}
