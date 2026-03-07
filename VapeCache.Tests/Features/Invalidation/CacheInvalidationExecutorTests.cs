using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Caching;
using VapeCache.Features.Invalidation;

namespace VapeCache.Tests.Features.Invalidation;

public sealed class CacheInvalidationExecutorTests
{
    [Fact]
    public async Task InvalidateAsync_SkipsWhenFeatureDisabled()
    {
        var cache = new RecordingVapeCache();
        var options = new MutableOptionsMonitor<CacheInvalidationOptions>(
            new CacheInvalidationOptions { Enabled = false });
        var executor = new CacheInvalidationExecutor(
            cache,
            options,
            NullLogger<CacheInvalidationExecutor>.Instance);

        var result = await executor.InvalidateAsync(
            new CacheInvalidationPlan(tags: ["t1"], zones: ["z1"], keys: ["k1"]));

        Assert.Equal(3, result.RequestedTargets);
        Assert.Equal(0, result.InvalidatedTargets);
        Assert.Equal(0, result.FailedTargets);
        Assert.Equal(3, result.SkippedTargets);
        Assert.Empty(cache.InvalidatedTags);
        Assert.Empty(cache.InvalidatedZones);
        Assert.Empty(cache.RemovedKeys);
    }

    [Fact]
    public async Task InvalidateAsync_ExecutesTagZoneAndKeyTargets()
    {
        var cache = new RecordingVapeCache();
        cache.SetMissingKey("k-missing");
        var options = new MutableOptionsMonitor<CacheInvalidationOptions>(new CacheInvalidationOptions());
        var executor = new CacheInvalidationExecutor(
            cache,
            options,
            NullLogger<CacheInvalidationExecutor>.Instance);

        var result = await executor.InvalidateAsync(
            new CacheInvalidationPlan(
                tags: ["t1"],
                zones: ["z1"],
                keys: ["k1", "k-missing"]));

        Assert.Equal(4, result.RequestedTargets);
        Assert.Equal(3, result.InvalidatedTargets);
        Assert.Equal(0, result.FailedTargets);
        Assert.Equal(1, result.SkippedTargets);
        Assert.Contains("t1", cache.InvalidatedTags);
        Assert.Contains("z1", cache.InvalidatedZones);
        Assert.Contains("k1", cache.RemovedKeys);
        Assert.Contains("k-missing", cache.RemovedKeys);
    }

    [Fact]
    public async Task InvalidateAsync_ThrowsWhenConfiguredToFailOnError()
    {
        var cache = new RecordingVapeCache
        {
            ThrowOnTag = true
        };
        var options = new MutableOptionsMonitor<CacheInvalidationOptions>(
            new CacheInvalidationOptions
            {
                Enabled = true,
                Profile = CacheInvalidationProfile.HighTrafficSite
            });
        var executor = new CacheInvalidationExecutor(
            cache,
            options,
            NullLogger<CacheInvalidationExecutor>.Instance);

        var exception = await Assert.ThrowsAsync<CacheInvalidationExecutionException>(async () =>
        {
            _ = await executor.InvalidateAsync(new CacheInvalidationPlan(tags: ["t1"]));
        });

        Assert.True(exception.Result.HasFailures);
        Assert.Equal(1, exception.Result.FailedTargets);
    }

    private sealed class RecordingVapeCache : IVapeCache
    {
        private readonly System.Threading.Lock _gate = new();
        private readonly HashSet<string> _missingKeys = new(StringComparer.Ordinal);

        public List<string> InvalidatedTags { get; } = [];

        public List<string> InvalidatedZones { get; } = [];

        public List<string> RemovedKeys { get; } = [];

        public bool ThrowOnTag { get; set; }

        public void SetMissingKey(string key) => _missingKeys.Add(key);

        public ICacheRegion Region(string name) => throw new NotSupportedException();

        public ValueTask<T?> GetAsync<T>(CacheKey<T> key, CancellationToken ct = default) => ValueTask.FromResult<T?>(default);

        public ValueTask SetAsync<T>(CacheKey<T> key, T value, CacheEntryOptions options = default, CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask<T> GetOrCreateAsync<T>(
            CacheKey<T> key,
            Func<CancellationToken, ValueTask<T>> factory,
            CacheEntryOptions options = default,
            CancellationToken ct = default) => throw new NotSupportedException();

        public ValueTask<bool> RemoveAsync(CacheKey key, CancellationToken ct = default)
        {
            lock (_gate)
                RemovedKeys.Add(key.Value);
            return ValueTask.FromResult(!_missingKeys.Contains(key.Value));
        }

        public ValueTask<long> InvalidateTagAsync(string tag, CancellationToken ct = default)
        {
            if (ThrowOnTag)
                throw new InvalidOperationException("tag failure");

            lock (_gate)
                InvalidatedTags.Add(tag);
            return ValueTask.FromResult(1L);
        }

        public ValueTask<long> GetTagVersionAsync(string tag, CancellationToken ct = default)
            => ValueTask.FromResult(1L);

        public ValueTask<long> InvalidateZoneAsync(string zone, CancellationToken ct = default)
        {
            lock (_gate)
                InvalidatedZones.Add(zone);
            return ValueTask.FromResult(1L);
        }

        public ValueTask<long> GetZoneVersionAsync(string zone, CancellationToken ct = default)
            => ValueTask.FromResult(1L);
    }

    private sealed class MutableOptionsMonitor<T>(T initialValue) : IOptionsMonitor<T>
    {
        private readonly System.Threading.Lock _gate = new();
        private T _value = initialValue;

        public T CurrentValue
        {
            get
            {
                lock (_gate)
                    return _value;
            }
        }

        public T Get(string? name) => CurrentValue;

        public IDisposable OnChange(Action<T, string?> listener)
            => NoopDisposable.Instance;

        public void Set(T nextValue)
        {
            lock (_gate)
                _value = nextValue;
        }

        private sealed class NoopDisposable : IDisposable
        {
            public static NoopDisposable Instance { get; } = new();

            public void Dispose()
            {
            }
        }
    }
}
