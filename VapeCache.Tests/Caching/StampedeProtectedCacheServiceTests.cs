using System.Buffers;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Caching;
using VapeCache.Infrastructure.Caching;
using Xunit;

namespace VapeCache.Tests.Caching;

public sealed class StampedeProtectedCacheServiceTests
{
    [Fact]
    public async Task GetOrSetAsync_is_single_flight_per_key()
    {
        var inner = new FakeCache();
        var svc = new StampedeProtectedCacheService(inner, Options.Create(new CacheStampedeOptions { Enabled = true }));

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
        var svc = new StampedeProtectedCacheService(inner, Options.Create(new CacheStampedeOptions { Enabled = true }));

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

        public ValueTask<T> GetOrSetAsync<T>(string key, Func<CancellationToken, ValueTask<T>> factory, Action<IBufferWriter<byte>, T> serialize,
            SpanDeserializer<T> deserialize, CacheEntryOptions options, CancellationToken ct)
            => throw new NotSupportedException();
    }
}
