using System.Buffers;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Options;
using VapeCache.Application.Caching;
using VapeCache.Infrastructure.Caching;

namespace VapeCache.Benchmarks.Benchmarks;

[MemoryDiagnoser]
public class StampedeProtectedCacheServiceBenchmarks
{
    private StampedeProtectedCacheService _svc = null!;
    private FakeCacheService _inner = null!;
    private CacheEntryOptions _entryOptions;

    private readonly Func<CancellationToken, ValueTask<int>> _factory = _ => new ValueTask<int>(42);
    private readonly Action<IBufferWriter<byte>, int> _serialize = static (w, v) =>
    {
        Span<byte> tmp = stackalloc byte[4];
        BitConverter.TryWriteBytes(tmp, v);
        w.Write(tmp);
    };

    private readonly Func<ReadOnlySpan<byte>, int> _deserialize = static s => BitConverter.ToInt32(s);

    [Params(false, true)]
    public bool Enabled { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _inner = new FakeCacheService();
        _svc = new StampedeProtectedCacheService(_inner, Options.Create(new CacheStampedeOptions { Enabled = Enabled, MaxKeys = 100_000 }));
        _entryOptions = new CacheEntryOptions(TimeSpan.FromMinutes(5));

        _inner.SetRaw("bench:hit", BitConverter.GetBytes(1234));
    }

    [IterationSetup(Target = nameof(GetOrSet_Hit))]
    public void HitIterationSetup()
        => _inner.SetRaw("bench:hit", BitConverter.GetBytes(1234));

    [IterationSetup(Target = nameof(GetOrSet_Miss))]
    public void MissIterationSetup()
        => _inner.RemoveRaw("bench:miss");

    [Benchmark]
    public async ValueTask<int> GetOrSet_Hit()
        => await _svc.GetOrSetAsync("bench:hit", _factory, _serialize, _deserialize, _entryOptions, CancellationToken.None)
            .ConfigureAwait(false);

    [Benchmark]
    public async ValueTask<int> GetOrSet_Miss()
        => await _svc.GetOrSetAsync("bench:miss", _factory, _serialize, _deserialize, _entryOptions, CancellationToken.None)
            .ConfigureAwait(false);

    private sealed class FakeCacheService : ICacheService
    {
        private readonly Dictionary<string, byte[]> _store = new(StringComparer.Ordinal);

        public string Name => "fake";

        public ValueTask<byte[]?> GetAsync(string key, CancellationToken ct)
        {
            _store.TryGetValue(key, out var value);
            return new ValueTask<byte[]?>(value);
        }

        public ValueTask SetAsync(string key, ReadOnlyMemory<byte> value, CacheEntryOptions options, CancellationToken ct)
        {
            _store[key] = value.ToArray();
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> RemoveAsync(string key, CancellationToken ct)
            => new(_store.Remove(key));

        public ValueTask<T?> GetAsync<T>(string key, Func<ReadOnlySpan<byte>, T> deserialize, CancellationToken ct)
        {
            if (!_store.TryGetValue(key, out var value))
                return new ValueTask<T?>(default(T));
            return new ValueTask<T?>(deserialize(value));
        }

        public ValueTask SetAsync<T>(string key, T value, Action<IBufferWriter<byte>, T> serialize, CacheEntryOptions options, CancellationToken ct)
        {
            var buffer = new ArrayBufferWriter<byte>(64);
            serialize(buffer, value);
            _store[key] = buffer.WrittenSpan.ToArray();
            return ValueTask.CompletedTask;
        }

        public async ValueTask<T> GetOrSetAsync<T>(
            string key,
            Func<CancellationToken, ValueTask<T>> factory,
            Action<IBufferWriter<byte>, T> serialize,
            Func<ReadOnlySpan<byte>, T> deserialize,
            CacheEntryOptions options,
            CancellationToken ct)
        {
            var bytes = await GetAsync(key, ct).ConfigureAwait(false);
            if (bytes is not null) return deserialize(bytes);

            var created = await factory(ct).ConfigureAwait(false);
            await SetAsync(key, created, serialize, options, ct).ConfigureAwait(false);
            return created;
        }

        public void SetRaw(string key, byte[] bytes) => _store[key] = bytes;
        public void RemoveRaw(string key) => _store.Remove(key);
    }
}
