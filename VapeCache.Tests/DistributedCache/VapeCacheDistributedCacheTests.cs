using System.Buffers;
using System.Buffers.Binary;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Caching;
using VapeCache.Extensions.DistributedCache;

namespace VapeCache.Tests.DistributedCache;

public sealed class VapeCacheDistributedCacheTests
{
    [Fact]
    public async Task SetAsync_WithAbsoluteExpirationRelativeToNow_RoundTripsPayload_AndStoresCorrectDeadline()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 4, 1, 12, 0, 0, TimeSpan.Zero));
        var backend = new RecordingCacheService(timeProvider);
        var sut = CreateSut(backend, timeProvider);
        var payload = "cold-brew"u8.ToArray();

        await sut.SetAsync(
            "product:1",
            payload,
            new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5)
            });

        var roundTrip = await sut.GetAsync("product:1");
        Assert.Equal(payload, roundTrip);
        Assert.Equal(timeProvider.GetUtcNow().AddMinutes(5), backend.GetStored("product:1").ExpiresAtUtc);
    }

    [Fact]
    public async Task SetAsync_WithExplicitAbsoluteExpiration_UsesAbsoluteDeadline()
    {
        var start = new DateTimeOffset(2026, 4, 1, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new ManualTimeProvider(start);
        var backend = new RecordingCacheService(timeProvider);
        var sut = CreateSut(backend, timeProvider);
        var payload = "americano"u8.ToArray();
        var absoluteExpiration = start.AddMinutes(3);

        await sut.SetAsync(
            "product:absolute",
            payload,
            new DistributedCacheEntryOptions
            {
                AbsoluteExpiration = absoluteExpiration
            });

        Assert.Equal(payload, await sut.GetAsync("product:absolute"));
        Assert.Equal(absoluteExpiration, backend.GetStored("product:absolute").ExpiresAtUtc);

        timeProvider.Advance(TimeSpan.FromMinutes(3).Add(TimeSpan.FromSeconds(1)));
        Assert.Null(await sut.GetAsync("product:absolute"));
    }

    [Fact]
    public async Task GetAsync_WithSlidingExpiration_RoundTripsPayload_AndRefreshesTtl()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 4, 1, 12, 0, 0, TimeSpan.Zero));
        var backend = new RecordingCacheService(timeProvider);
        var sut = CreateSut(backend, timeProvider);
        var payload = "fusion:l2"u8.ToArray();

        await sut.SetAsync(
            "fusion:key",
            payload,
            new DistributedCacheEntryOptions
            {
                SlidingExpiration = TimeSpan.FromSeconds(10)
            });

        var stored = backend.GetStored("fusion:key");
        Assert.NotEqual(payload, stored.Payload);
        Assert.Equal(timeProvider.GetUtcNow().AddSeconds(10), stored.ExpiresAtUtc);

        timeProvider.Advance(TimeSpan.FromSeconds(4));
        var value = await sut.GetAsync("fusion:key");

        Assert.Equal(payload, value);
        Assert.Equal(timeProvider.GetUtcNow().AddSeconds(10), backend.GetStored("fusion:key").ExpiresAtUtc);
    }

    [Fact]
    public async Task RefreshAsync_WithSlidingAndAbsoluteCap_DoesNotExtendPastAbsoluteExpiration()
    {
        var start = new DateTimeOffset(2026, 4, 1, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new ManualTimeProvider(start);
        var backend = new RecordingCacheService(timeProvider);
        var sut = CreateSut(backend, timeProvider);
        var payload = "session"u8.ToArray();

        await sut.SetAsync(
            "session:1",
            payload,
            new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30),
                SlidingExpiration = TimeSpan.FromSeconds(20)
            });

        timeProvider.Advance(TimeSpan.FromSeconds(15));
        await sut.RefreshAsync("session:1");

        Assert.Equal(start.AddSeconds(30), backend.GetStored("session:1").ExpiresAtUtc);

        timeProvider.Advance(TimeSpan.FromSeconds(14));
        Assert.Equal(payload, await sut.GetAsync("session:1"));

        timeProvider.Advance(TimeSpan.FromSeconds(2));
        Assert.Null(await sut.GetAsync("session:1"));
    }

    [Fact]
    public async Task BufferDistributedCache_RoundTripsMultiSegmentPayload()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 4, 1, 12, 0, 0, TimeSpan.Zero));
        var backend = new RecordingCacheService(timeProvider);
        var sut = CreateSut(backend, timeProvider);
        var bufferCache = (IBufferDistributedCache)sut;
        var payload = "payload-seq"u8.ToArray();
        var sequence = CreateSequence("payload-"u8.ToArray(), "seq"u8.ToArray());

        await bufferCache.SetAsync(
            "buffer:1",
            sequence,
            new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1)
            });

        var writer = new ArrayBufferWriter<byte>();
        var found = await bufferCache.TryGetAsync("buffer:1", writer, CancellationToken.None);

        Assert.True(found);
        Assert.Equal(payload, writer.WrittenSpan.ToArray());
    }

    [Fact]
    public void BufferDistributedCache_SyncTryGet_RoundTripsMultiSegmentPayload()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 4, 1, 12, 0, 0, TimeSpan.Zero));
        var backend = new RecordingCacheService(timeProvider);
        var sut = CreateSut(backend, timeProvider);
        var bufferCache = (IBufferDistributedCache)sut;
        var payload = "buffer-sync"u8.ToArray();
        var sequence = CreateSequence("buffer-"u8.ToArray(), "sync"u8.ToArray());

        bufferCache.Set(
            "buffer:sync",
            sequence,
            new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1)
            });

        var writer = new ArrayBufferWriter<byte>();
        var found = bufferCache.TryGet("buffer:sync", writer);

        Assert.True(found);
        Assert.Equal(payload, writer.WrittenSpan.ToArray());
    }

    [Fact]
    public async Task GetAsync_WithMalformedCurrentEnvelope_TreatsEntryAsMiss_AndRemovesCorruptValue()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 4, 1, 12, 0, 0, TimeSpan.Zero));
        var backend = new RecordingCacheService(timeProvider);
        var sut = CreateSut(backend, timeProvider);

        await backend.SetAsync(
            "corrupt:1",
            CreateMalformedCurrentEnvelope(),
            new CacheEntryOptions(TimeSpan.FromMinutes(1)),
            CancellationToken.None);

        Assert.Null(await sut.GetAsync("corrupt:1"));
        Assert.Null(backend.TryGetStored("corrupt:1"));
    }

    [Fact]
    public async Task SetAsync_WithPayloadThatLooksLikeLegacySlidingEnvelope_RoundTripsUnchanged()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 4, 1, 12, 0, 0, TimeSpan.Zero));
        var backend = new RecordingCacheService(timeProvider);
        var sut = CreateSut(backend, timeProvider);
        var payload = CreateLegacySlidingEnvelopePayload("raw-inner"u8.ToArray(), slidingExpiration: TimeSpan.FromSeconds(30));

        await sut.SetAsync(
            "raw:legacy-shaped",
            payload,
            new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1)
            });

        var roundTrip = await sut.GetAsync("raw:legacy-shaped");
        Assert.Equal(payload, roundTrip);
    }

    [Fact]
    public void SyncApi_RoundTripsSlidingPayload_AndRefreshesTtl()
    {
        var start = new DateTimeOffset(2026, 4, 1, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new ManualTimeProvider(start);
        var backend = new RecordingCacheService(timeProvider);
        var sut = CreateSut(backend, timeProvider);
        var payload = "sync-sliding"u8.ToArray();

        sut.Set(
            "sync:1",
            payload,
            new DistributedCacheEntryOptions
            {
                SlidingExpiration = TimeSpan.FromSeconds(10)
            });

        timeProvider.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(payload, sut.Get("sync:1"));
        Assert.Equal(timeProvider.GetUtcNow().AddSeconds(10), backend.GetStored("sync:1").ExpiresAtUtc);

        timeProvider.Advance(TimeSpan.FromSeconds(6));
        sut.Refresh("sync:1");
        Assert.Equal(timeProvider.GetUtcNow().AddSeconds(10), backend.GetStored("sync:1").ExpiresAtUtc);

        timeProvider.Advance(TimeSpan.FromSeconds(11));
        Assert.Null(sut.Get("sync:1"));
    }

    [Fact]
    public void Remove_WithKeyPrefix_RemovesPrefixedEntry()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 4, 1, 12, 0, 0, TimeSpan.Zero));
        var backend = new RecordingCacheService(timeProvider);
        var sut = CreateSut(backend, timeProvider, keyPrefix: "app:");

        sut.Set(
            "cart:1",
            "items"u8.ToArray(),
            new DistributedCacheEntryOptions());

        Assert.NotNull(backend.TryGetStored("app:cart:1"));

        sut.Remove("cart:1");

        Assert.Null(backend.TryGetStored("app:cart:1"));
    }

    private static VapeCacheDistributedCache CreateSut(
        RecordingCacheService backend,
        TimeProvider timeProvider,
        string keyPrefix = "")
        => new(
            backend,
            timeProvider,
            Options.Create(new VapeCacheDistributedCacheOptions
            {
                KeyPrefix = keyPrefix
            }));

    private static ReadOnlySequence<byte> CreateSequence(ReadOnlyMemory<byte> first, ReadOnlyMemory<byte> second)
    {
        var firstSegment = new BufferSegment(first);
        var secondSegment = firstSegment.Append(second);
        return new ReadOnlySequence<byte>(firstSegment, 0, secondSegment, second.Length);
    }

    private static byte[] CreateMalformedCurrentEnvelope()
    {
        var prefix = "VapeCache:IDC:2:"u8.ToArray();
        var buffer = new byte[prefix.Length + sizeof(byte) + sizeof(long)];
        prefix.CopyTo(buffer, 0);
        buffer[prefix.Length] = 0x01;
        BinaryPrimitives.WriteInt64LittleEndian(
            buffer.AsSpan(prefix.Length + sizeof(byte), sizeof(long)),
            DateTime.UnixEpoch.AddMinutes(1).Ticks);
        return buffer;
    }

    private static byte[] CreateLegacySlidingEnvelopePayload(ReadOnlySpan<byte> payload, TimeSpan slidingExpiration, DateTimeOffset? absoluteExpiration = null)
    {
        var prefix = "VapeCache:IDC:1:"u8.ToArray();
        var totalLength = prefix.Length + sizeof(long) + sizeof(long) + sizeof(int) + payload.Length;
        var buffer = new byte[totalLength];
        var span = buffer.AsSpan();
        var offset = 0;

        prefix.CopyTo(span);
        offset += prefix.Length;
        BinaryPrimitives.WriteInt64LittleEndian(
            span.Slice(offset, sizeof(long)),
            absoluteExpiration?.UtcDateTime.Ticks ?? 0);
        offset += sizeof(long);
        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(offset, sizeof(long)), slidingExpiration.Ticks);
        offset += sizeof(long);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset, sizeof(int)), payload.Length);
        offset += sizeof(int);
        payload.CopyTo(span.Slice(offset));
        return buffer;
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public ManualTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan amount)
        {
            _utcNow = _utcNow.Add(amount);
        }
    }

    private sealed class RecordingCacheService : ICacheService
    {
        private readonly Dictionary<string, StoredEntry> _entries = new(StringComparer.Ordinal);
        private readonly TimeProvider _timeProvider;

        public RecordingCacheService(TimeProvider timeProvider)
        {
            _timeProvider = timeProvider;
        }

        public string Name => "recording";

        public StoredEntry GetStored(string key)
            => TryGetStored(key) ?? throw new InvalidOperationException($"Stored key '{key}' was not found.");

        public StoredEntry? TryGetStored(string key)
        {
            PurgeExpired(key);
            return _entries.TryGetValue(key, out var entry) ? entry with { Payload = entry.Payload.ToArray() } : null;
        }

        public ValueTask<byte[]?> GetAsync(string key, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            PurgeExpired(key);
            return ValueTask.FromResult(_entries.TryGetValue(key, out var entry) ? entry.Payload.ToArray() : null);
        }

        public ValueTask SetAsync(string key, ReadOnlyMemory<byte> value, CacheEntryOptions options, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var expiresAtUtc = options.Ttl.HasValue ? _timeProvider.GetUtcNow().Add(options.Ttl.Value) : (DateTimeOffset?)null;
            _entries[key] = new StoredEntry(value.ToArray(), expiresAtUtc);
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> RemoveAsync(string key, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_entries.Remove(key));
        }

        public async ValueTask<T?> GetAsync<T>(string key, SpanDeserializer<T> deserialize, CancellationToken ct)
        {
            var payload = await GetAsync(key, ct).ConfigureAwait(false);
            return payload is null ? default : deserialize(payload);
        }

        public ValueTask SetAsync<T>(string key, T value, Action<IBufferWriter<byte>, T> serialize, CacheEntryOptions options, CancellationToken ct)
        {
            var writer = new ArrayBufferWriter<byte>();
            serialize(writer, value);
            return SetAsync(key, writer.WrittenMemory, options, ct);
        }

        public async ValueTask<T> GetOrSetAsync<T>(
            string key,
            Func<CancellationToken, ValueTask<T>> factory,
            Action<IBufferWriter<byte>, T> serialize,
            SpanDeserializer<T> deserialize,
            CacheEntryOptions options,
            CancellationToken ct)
        {
            var existing = await GetAsync(key, deserialize, ct).ConfigureAwait(false);
            if (existing is not null)
                return existing;

            var created = await factory(ct).ConfigureAwait(false);
            await SetAsync(key, created, serialize, options, ct).ConfigureAwait(false);
            return created;
        }

        private void PurgeExpired(string key)
        {
            if (!_entries.TryGetValue(key, out var entry))
                return;

            if (entry.ExpiresAtUtc.HasValue && entry.ExpiresAtUtc.Value <= _timeProvider.GetUtcNow())
                _entries.Remove(key);
        }
    }

    private sealed record StoredEntry(byte[] Payload, DateTimeOffset? ExpiresAtUtc);

    private sealed class BufferSegment : ReadOnlySequenceSegment<byte>
    {
        public BufferSegment(ReadOnlyMemory<byte> memory)
        {
            Memory = memory;
        }

        public BufferSegment Append(ReadOnlyMemory<byte> memory)
        {
            var segment = new BufferSegment(memory)
            {
                RunningIndex = RunningIndex + Memory.Length
            };
            Next = segment;
            return segment;
        }
    }
}
