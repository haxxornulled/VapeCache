using System.Buffers;
using VapeCache.Abstractions.Caching;
using VapeCache.Infrastructure.Caching;
using VapeCache.Tests.Infrastructure;
using Xunit;

namespace VapeCache.Tests.Caching;

public sealed class StampedeProtectedCacheServiceTests
{
    [Fact]
    public async Task GetOrSetAsync_is_single_flight_per_key()
    {
        var inner = new FakeCache();
        var svc = new StampedeProtectedCacheService(inner, new TestOptionsMonitor<CacheStampedeOptions>(new CacheStampedeOptions { Enabled = true }));

        var calls = 0;
        ValueTask<int> Factory(CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            return ValueTask.FromResult(123);
        }

        static void Serialize(IBufferWriter<byte> w, int v)
        {
            var span = w.GetSpan(4);
            BitConverter.TryWriteBytes(span, v);
            w.Advance(4);
        }

        static int Deserialize(ReadOnlySpan<byte> s) => BitConverter.ToInt32(s);

        var tasks = Enumerable.Range(0, 50).Select(_ => svc.GetOrSetAsync("k", Factory, Serialize, Deserialize, new CacheEntryOptions(TimeSpan.FromMinutes(1)), CancellationToken.None).AsTask());
        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.Equal(123, r));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task GetOrSetAsync_releases_lock_on_factory_failure()
    {
        var inner = new FakeCache();
        var svc = new StampedeProtectedCacheService(inner, new TestOptionsMonitor<CacheStampedeOptions>(new CacheStampedeOptions
        {
            Enabled = true,
            EnableFailureBackoff = false
        }));

        var first = true;
        async ValueTask<int> Factory(CancellationToken ct)
        {
            await Task.Delay(10, ct);
            if (first)
            {
                first = false;
                throw new InvalidOperationException("boom");
            }
            return 7;
        }

        static void Serialize(IBufferWriter<byte> w, int v)
        {
            var span = w.GetSpan(4);
            BitConverter.TryWriteBytes(span, v);
            w.Advance(4);
        }

        static int Deserialize(ReadOnlySpan<byte> s) => BitConverter.ToInt32(s);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.GetOrSetAsync("k2", Factory, Serialize, Deserialize, new CacheEntryOptions(TimeSpan.FromMinutes(1)), CancellationToken.None).AsTask());

        var ok = await svc.GetOrSetAsync("k2", Factory, Serialize, Deserialize, new CacheEntryOptions(TimeSpan.FromMinutes(1)), CancellationToken.None);
        Assert.Equal(7, ok);
    }

    [Fact]
    public async Task GetOrSetAsync_releases_lock_when_wait_is_canceled()
    {
        var inner = new FakeCache();
        var svc = new StampedeProtectedCacheService(inner, new TestOptionsMonitor<CacheStampedeOptions>(new CacheStampedeOptions { Enabled = true }));

        static void Serialize(IBufferWriter<byte> w, int v)
        {
            var span = w.GetSpan(4);
            BitConverter.TryWriteBytes(span, v);
            w.Advance(4);
        }

        static int Deserialize(ReadOnlySpan<byte> s) => BitConverter.ToInt32(s);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            svc.GetOrSetAsync(
                "cancel-key",
                _ => ValueTask.FromResult(42),
                Serialize,
                Deserialize,
                new CacheEntryOptions(TimeSpan.FromMinutes(1)),
                cts.Token).AsTask());

        var value = await svc.GetOrSetAsync(
            "cancel-key",
            _ => ValueTask.FromResult(7),
            Serialize,
            Deserialize,
            new CacheEntryOptions(TimeSpan.FromMinutes(1)),
            CancellationToken.None);

        Assert.Equal(7, value);
    }

    [Fact]
    public async Task GetOrSetAsync_rejects_suspicious_key_when_enabled()
    {
        var inner = new FakeCache();
        var svc = new StampedeProtectedCacheService(inner, new TestOptionsMonitor<CacheStampedeOptions>(new CacheStampedeOptions
        {
            Enabled = true,
            RejectSuspiciousKeys = true
        }));

        static void Serialize(IBufferWriter<byte> w, int v)
        {
            var span = w.GetSpan(4);
            BitConverter.TryWriteBytes(span, v);
            w.Advance(4);
        }

        static int Deserialize(ReadOnlySpan<byte> s) => BitConverter.ToInt32(s);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.GetOrSetAsync(
                "bad\u0000key",
                _ => ValueTask.FromResult(1),
                Serialize,
                Deserialize,
                new CacheEntryOptions(TimeSpan.FromMinutes(1)),
                CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task GetOrSetAsync_applies_failure_backoff_after_factory_error()
    {
        var inner = new FakeCache();
        var svc = new StampedeProtectedCacheService(inner, new TestOptionsMonitor<CacheStampedeOptions>(new CacheStampedeOptions
        {
            Enabled = true,
            EnableFailureBackoff = true,
            FailureBackoff = TimeSpan.FromMilliseconds(200)
        }));

        static void Serialize(IBufferWriter<byte> w, int v)
        {
            var span = w.GetSpan(4);
            BitConverter.TryWriteBytes(span, v);
            w.Advance(4);
        }

        static int Deserialize(ReadOnlySpan<byte> s) => BitConverter.ToInt32(s);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.GetOrSetAsync("bk", _ => ValueTask.FromException<int>(new InvalidOperationException("boom")), Serialize, Deserialize, new CacheEntryOptions(TimeSpan.FromMinutes(1)), CancellationToken.None).AsTask());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.GetOrSetAsync("bk", _ => ValueTask.FromResult(2), Serialize, Deserialize, new CacheEntryOptions(TimeSpan.FromMinutes(1)), CancellationToken.None).AsTask());

        await Task.Delay(250);
        var ok = await svc.GetOrSetAsync("bk", _ => ValueTask.FromResult(3), Serialize, Deserialize, new CacheEntryOptions(TimeSpan.FromMinutes(1)), CancellationToken.None);
        Assert.Equal(3, ok);
    }

    [Fact]
    public async Task GetOrSetAsync_lock_wait_timeout_throws_timeout_and_increments_stat()
    {
        var inner = new FakeCache();
        var stats = new CacheStats();
        var svc = new StampedeProtectedCacheService(
            inner,
            new TestOptionsMonitor<CacheStampedeOptions>(new CacheStampedeOptions
            {
                Enabled = true,
                LockWaitTimeout = TimeSpan.FromMilliseconds(75)
            }),
            stats);

        static void Serialize(IBufferWriter<byte> w, int v)
        {
            var span = w.GetSpan(4);
            BitConverter.TryWriteBytes(span, v);
            w.Advance(4);
        }

        static int Deserialize(ReadOnlySpan<byte> s) => BitConverter.ToInt32(s);

        var firstEnteredFactory = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = svc.GetOrSetAsync(
            "timeout-key",
            async _ =>
            {
                firstEnteredFactory.TrySetResult();
                await releaseFirst.Task;
                return 1;
            },
            Serialize,
            Deserialize,
            new CacheEntryOptions(TimeSpan.FromMinutes(1)),
            CancellationToken.None).AsTask();

        await firstEnteredFactory.Task;

        await Assert.ThrowsAsync<TimeoutException>(() =>
            svc.GetOrSetAsync(
                "timeout-key",
                _ => ValueTask.FromResult(2),
                Serialize,
                Deserialize,
                new CacheEntryOptions(TimeSpan.FromMinutes(1)),
                CancellationToken.None).AsTask());

        Assert.Equal(1, stats.Snapshot.StampedeLockWaitTimeout);

        releaseFirst.TrySetResult();
        var firstValue = await first;
        Assert.Equal(1, firstValue);
    }

    private sealed class FakeCache : ICacheService
    {
        private readonly Dictionary<string, byte[]> _store = new(StringComparer.Ordinal);
        public string Name => "fake";

        public ValueTask<byte[]?> GetAsync(string key, CancellationToken ct)
        {
            _store.TryGetValue(key, out var v);
            return ValueTask.FromResult<byte[]?>(v);
        }

        public ValueTask SetAsync(string key, ReadOnlyMemory<byte> value, CacheEntryOptions options, CancellationToken ct)
        {
            _store[key] = value.ToArray();
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> RemoveAsync(string key, CancellationToken ct)
        {
            var ok = _store.Remove(key);
            return ValueTask.FromResult(ok);
        }

        public ValueTask<T?> GetAsync<T>(string key, SpanDeserializer<T> deserialize, CancellationToken ct)
        {
            if (!_store.TryGetValue(key, out var v)) return ValueTask.FromResult<T?>(default);
            return ValueTask.FromResult<T?>(deserialize(v));
        }

        public ValueTask SetAsync<T>(string key, T value, Action<IBufferWriter<byte>, T> serialize, CacheEntryOptions options, CancellationToken ct)
        {
            var abw = new ArrayBufferWriter<byte>(64);
            serialize(abw, value);
            _store[key] = abw.WrittenSpan.ToArray();
            return ValueTask.CompletedTask;
        }

        public async ValueTask<T> GetOrSetAsync<T>(string key, Func<CancellationToken, ValueTask<T>> factory, Action<IBufferWriter<byte>, T> serialize,
            SpanDeserializer<T> deserialize, CacheEntryOptions options, CancellationToken ct)
        {
            if (_store.TryGetValue(key, out var existing))
                return deserialize(existing);

            var created = await factory(ct).ConfigureAwait(false);
            var abw = new ArrayBufferWriter<byte>(64);
            serialize(abw, created);
            _store[key] = abw.WrittenSpan.ToArray();
            return created;
        }
    }
}
