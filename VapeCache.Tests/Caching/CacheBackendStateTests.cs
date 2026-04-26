using VapeCache.Abstractions.Caching;
using VapeCache.Abstractions.Diagnostics;
using VapeCache.Infrastructure.Caching;

namespace VapeCache.Tests.Caching;

public sealed class CacheBackendStateTests
{
    [Fact]
    public void EffectiveBackend_follows_current_runtime_backend_when_hybrid_is_healthy()
    {
        var current = new CurrentCacheService();
        var breaker = new TestBreakerState(isOpen: false);
        var failover = new TestFailoverController(isForcedOpen: false);
        var sut = new CacheBackendState(current, breaker, failover);

        current.SetCurrent("memory");
        Assert.Equal(BackendType.InMemory, sut.EffectiveBackend);

        current.SetCurrent("redis");
        Assert.Equal(BackendType.Redis, sut.EffectiveBackend);
    }

    [Fact]
    public void EffectiveBackend_reports_memory_when_breaker_is_open()
    {
        var current = new CurrentCacheService();
        current.SetCurrent("redis");
        var sut = new CacheBackendState(
            current,
            new TestBreakerState(isOpen: true),
            new TestFailoverController(isForcedOpen: false));

        Assert.Equal(BackendType.InMemory, sut.EffectiveBackend);
    }

    [Fact]
    public void EffectiveBackend_reports_memory_when_failover_is_forced_open()
    {
        var current = new CurrentCacheService();
        current.SetCurrent("redis");
        var sut = new CacheBackendState(
            current,
            new TestBreakerState(isOpen: false),
            new TestFailoverController(isForcedOpen: true));

        Assert.Equal(BackendType.InMemory, sut.EffectiveBackend);
    }

    private sealed class TestBreakerState : IRedisCircuitBreakerState
    {
        public TestBreakerState(bool isOpen)
        {
            IsOpen = isOpen;
        }

        public bool Enabled => true;
        public bool IsOpen { get; }
        public int ConsecutiveFailures => 0;
        public TimeSpan? OpenRemaining => null;
        public bool HalfOpenProbeInFlight => false;
    }

    private sealed class TestFailoverController : IRedisFailoverController
    {
        public TestFailoverController(bool isForcedOpen)
        {
            IsForcedOpen = isForcedOpen;
        }

        public bool IsForcedOpen { get; private set; }
        public string? Reason => null;
        public void ForceOpen(string reason) => IsForcedOpen = true;
        public void ClearForcedOpen() => IsForcedOpen = false;
        public void MarkRedisSuccess() { }
        public void MarkRedisFailure() { }
    }
}
