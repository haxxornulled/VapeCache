using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using BenchmarkDotNet.Attributes;
using VapeCache.Abstractions.Caching;
using VapeCache.Infrastructure.Caching;

namespace VapeCache.Benchmarks.Benchmarks;

[MemoryDiagnoser]
public class CacheHotKeyContentionBenchmarks
{
    private const string HotKey = "bench:hot";
    private ContentionCacheService _raw = null!;
    private StampedeProtectedCacheService _protected = null!;
    private CacheEntryOptions _entryOptions;

    [Params(8, 32)]
    public int Concurrency { get; set; }

    [Params(0, 200)]
    public int FactorySpinMicros { get; set; }

    private readonly Action<IBufferWriter<byte>, int> _serialize = static (writer, value) =>
    {
        Span<byte> buffer = stackalloc byte[4];
        BitConverter.TryWriteBytes(buffer, value);
        writer.Write(buffer);
    };

    private readonly SpanDeserializer<int> _deserialize = static data => BitConverter.ToInt32(data);

    [GlobalSetup]
    public void Setup()
    {
        _raw = new ContentionCacheService();
        _protected = new StampedeProtectedCacheService(
            _raw,
            new BenchmarkOptionsMonitor<CacheStampedeOptions>(new CacheStampedeOptions
            {
                Enabled = true,
                MaxKeys = 100_000,
                LockWaitTimeout = TimeSpan.FromSeconds(5)
            }));
        _entryOptions = new CacheEntryOptions(TimeSpan.FromMinutes(5));
    }

    [IterationSetup]
    public void Reset()
        => _raw.RemoveRaw(HotKey);

    [Benchmark(Description = "GetOrSetAsync Hot-Key Burst - Raw")]
    public Task GetOrSet_HotKeyBurst_Raw()
        => RunBurstAsync(_raw);

    [Benchmark(Description = "GetOrSetAsync Hot-Key Burst - StampedeProtected")]
    public Task GetOrSet_HotKeyBurst_Protected()
        => RunBurstAsync(_protected);

    private async Task RunBurstAsync(ICacheService cache)
    {
        _raw.RemoveRaw(HotKey);

        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = new Task[Concurrency];

        for (var i = 0; i < tasks.Length; i++)
            tasks[i] = RunSingleAsync(cache, startGate.Task);

        startGate.SetResult();
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task RunSingleAsync(ICacheService cache, Task startGate)
    {
        await startGate.ConfigureAwait(false);
        var value = await cache.GetOrSetAsync(
            HotKey,
            Factory,
            _serialize,
            _deserialize,
            _entryOptions,
            CancellationToken.None).ConfigureAwait(false);

        if (value != 42)
            throw new InvalidOperationException($"Unexpected benchmark value: {value}.");
    }

    private ValueTask<int> Factory(CancellationToken ct)
    {
        if (FactorySpinMicros > 0)
            SpinForMicros(FactorySpinMicros);

        return ValueTask.FromResult(42);
    }

    private static void SpinForMicros(int microseconds)
    {
        var targetTicks = (long)Math.Ceiling((microseconds / 1_000_000d) * Stopwatch.Frequency);
        if (targetTicks <= 0)
            return;

        var start = Stopwatch.GetTimestamp();
        while (Stopwatch.GetTimestamp() - start < targetTicks)
            Thread.SpinWait(64);
    }

    private sealed class ContentionCacheService : ICacheService
    {
        private readonly ConcurrentDictionary<string, byte[]> _store = new(StringComparer.Ordinal);

        public string Name => "contention";

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
            => new(_store.TryRemove(key, out _));

        public ValueTask<T?> GetAsync<T>(string key, SpanDeserializer<T> deserialize, CancellationToken ct)
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
            SpanDeserializer<T> deserialize,
            CacheEntryOptions options,
            CancellationToken ct)
        {
            var existing = await GetAsync(key, ct).ConfigureAwait(false);
            if (existing is not null)
                return deserialize(existing);

            var created = await factory(ct).ConfigureAwait(false);
            await SetAsync(key, created, serialize, options, ct).ConfigureAwait(false);
            return created;
        }

        public void RemoveRaw(string key)
            => _store.TryRemove(key, out _);
    }
}
