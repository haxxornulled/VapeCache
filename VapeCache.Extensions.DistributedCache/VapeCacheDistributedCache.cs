using System.Buffers;
using System.Buffers.Binary;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Caching;

namespace VapeCache.Extensions.DistributedCache;

/// <summary>
/// Bridge implementation of <see cref="IDistributedCache"/> and <see cref="IBufferDistributedCache"/>
/// backed by the VapeCache runtime.
/// </summary>
public sealed class VapeCacheDistributedCache : IDistributedCache, IBufferDistributedCache
{
    private static readonly byte[] EnvelopePrefix = "VapeCache:IDC:2:"u8.ToArray();
    private static readonly byte[] LegacySlidingEnvelopePrefix = "VapeCache:IDC:1:"u8.ToArray();
    private const byte EnvelopeFlagSliding = 0x01;
    private static readonly CacheIntent DistributedCacheIntent = new(
        CacheIntentKind.ComputedView,
        Reason: "idistributedcache-adapter",
        Owner: "VapeCache.Extensions.DistributedCache",
        Tags: ["distributed-cache", "interop"]);

    private readonly ICacheService _cache;
    private readonly TimeProvider _timeProvider;
    private readonly string _keyPrefix;
    private readonly ICacheOperationOriginAccessor? _originAccessor;

    /// <summary>
    /// Creates a new adapter instance.
    /// </summary>
    public VapeCacheDistributedCache(
        ICacheService cache,
        TimeProvider timeProvider,
        IOptions<VapeCacheDistributedCacheOptions>? options = null,
        ICacheOperationOriginAccessor? originAccessor = null)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _keyPrefix = options?.Value.KeyPrefix ?? string.Empty;
        _originAccessor = originAccessor;
    }

    /// <inheritdoc />
    public byte[]? Get(string key)
    {
        using var _ = BeginOriginScope();
        var result = GetSync(GetCoreAsync(key, refreshSliding: true, CancellationToken.None));
        return result.HasValue ? result.ToArray() : null;
    }

    /// <inheritdoc />
    public async Task<byte[]?> GetAsync(string key, CancellationToken token = default)
    {
        using var _ = BeginOriginScope();
        var result = await GetCoreAsync(key, refreshSliding: true, token).ConfigureAwait(false);
        return result.HasValue ? result.ToArray() : null;
    }

    /// <inheritdoc />
    public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
    {
        ArgumentNullException.ThrowIfNull(value);
        using var _ = BeginOriginScope();
        WaitSync(SetCoreAsync(key, value, options, CancellationToken.None));
    }

    /// <inheritdoc />
    public async Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        using var _ = BeginOriginScope();
        await SetCoreAsync(key, value, options, token).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Refresh(string key)
    {
        using var _ = BeginOriginScope();
        WaitSync(RefreshCoreAsync(key, CancellationToken.None));
    }

    /// <inheritdoc />
    public async Task RefreshAsync(string key, CancellationToken token = default)
    {
        using var _ = BeginOriginScope();
        await RefreshCoreAsync(key, token).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Remove(string key)
    {
        using var _ = BeginOriginScope();
        WaitSync(RemoveCoreAsync(key, CancellationToken.None));
    }

    /// <inheritdoc />
    public async Task RemoveAsync(string key, CancellationToken token = default)
    {
        using var _ = BeginOriginScope();
        await RemoveCoreAsync(key, token).ConfigureAwait(false);
    }

    bool IBufferDistributedCache.TryGet(string key, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        using var _ = BeginOriginScope();

        var result = GetSync(GetCoreAsync(key, refreshSliding: true, CancellationToken.None));
        if (!result.HasValue)
            return false;

        result.CopyTo(destination);
        return true;
    }

    async ValueTask<bool> IBufferDistributedCache.TryGetAsync(
        string key,
        IBufferWriter<byte> destination,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(destination);
        using var _ = BeginOriginScope();

        var result = await GetCoreAsync(key, refreshSliding: true, token).ConfigureAwait(false);
        if (!result.HasValue)
            return false;

        result.CopyTo(destination);
        return true;
    }

    void IBufferDistributedCache.Set(string key, ReadOnlySequence<byte> value, DistributedCacheEntryOptions options)
    {
        using var _ = BeginOriginScope();
        WaitSync(SetSequenceCoreAsync(key, value, options, CancellationToken.None));
    }

    async ValueTask IBufferDistributedCache.SetAsync(
        string key,
        ReadOnlySequence<byte> value,
        DistributedCacheEntryOptions options,
        CancellationToken token)
    {
        using var _ = BeginOriginScope();
        await SetSequenceCoreAsync(key, value, options, token).ConfigureAwait(false);
    }

    private IDisposable? BeginOriginScope()
        => _originAccessor?.BeginScope(CacheOperationOrigin.DistributedCacheBridge);

    private async ValueTask<CacheReadResult> GetCoreAsync(
        string key,
        bool refreshSliding,
        CancellationToken ct)
    {
        var cacheKey = NormalizeKey(key);
        var stored = await _cache.GetAsync(cacheKey, ct).ConfigureAwait(false);
        if (stored is null)
            return default;

        if (TryReadEnvelope(stored, out var envelope))
            return await ReadEnvelopeAsync(cacheKey, envelope, refreshSliding, ct).ConfigureAwait(false);

        if (HasEnvelopePrefix(stored))
        {
            await _cache.RemoveAsync(cacheKey, ct).ConfigureAwait(false);
            return default;
        }

        if (!TryReadLegacySlidingEnvelope(stored, out var legacyEnvelope))
            return new CacheReadResult(stored);

        return await ReadLegacySlidingEnvelopeAsync(cacheKey, legacyEnvelope, refreshSliding, ct).ConfigureAwait(false);
    }

    private async ValueTask SetCoreAsync(
        string key,
        ReadOnlyMemory<byte> value,
        DistributedCacheEntryOptions options,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);

        var cacheKey = NormalizeKey(key);
        var expiration = BuildExpirationPlan(options);

        if (expiration.RemoveImmediately)
        {
            await _cache.RemoveAsync(cacheKey, ct).ConfigureAwait(false);
            return;
        }

        if (expiration.RequiresSlidingEnvelope)
        {
            var envelope = CreateEnvelope(
                value.Span,
                expiration.AbsoluteExpirationUtcTicks,
                expiration.SlidingExpirationTicks,
                isSliding: true);
            await _cache.SetAsync(cacheKey, envelope, CreateCacheEntryOptions(expiration.InitialTtl), ct).ConfigureAwait(false);
            return;
        }

        var rawEnvelope = CreateEnvelope(
            value.Span,
            expiration.AbsoluteExpirationUtcTicks,
            slidingExpirationTicks: 0,
            isSliding: false);
        await _cache.SetAsync(cacheKey, rawEnvelope, CreateCacheEntryOptions(expiration.InitialTtl), ct).ConfigureAwait(false);
    }

    private ValueTask SetSequenceCoreAsync(
        string key,
        ReadOnlySequence<byte> value,
        DistributedCacheEntryOptions options,
        CancellationToken ct)
    {
        if (value.IsSingleSegment)
            return SetCoreAsync(key, value.First, options, ct);

        if (value.Length > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(value), "Sequence length exceeds the maximum supported payload size.");

        var buffer = GC.AllocateUninitializedArray<byte>((int)value.Length);
        value.CopyTo(buffer);
        return SetCoreAsync(key, buffer, options, ct);
    }

    private async ValueTask RefreshCoreAsync(string key, CancellationToken ct)
    {
        var cacheKey = NormalizeKey(key);
        var stored = await _cache.GetAsync(cacheKey, ct).ConfigureAwait(false);
        if (stored is null)
            return;

        if (TryReadEnvelope(stored, out var envelope))
        {
            if (!envelope.IsSliding)
                return;

            if (!TryComputeRefreshTtl(envelope.AbsoluteExpirationUtcTicks, envelope.SlidingExpirationTicks, _timeProvider.GetUtcNow(), out var refreshTtl))
            {
                await _cache.RemoveAsync(cacheKey, ct).ConfigureAwait(false);
                return;
            }

            var refreshed = CreateEnvelope(
                envelope.Payload.Span,
                envelope.AbsoluteExpirationUtcTicks,
                envelope.SlidingExpirationTicks,
                isSliding: true);
            await _cache.SetAsync(cacheKey, refreshed, CreateCacheEntryOptions(refreshTtl), ct).ConfigureAwait(false);
            return;
        }

        if (HasEnvelopePrefix(stored))
        {
            await _cache.RemoveAsync(cacheKey, ct).ConfigureAwait(false);
            return;
        }

        if (!TryReadLegacySlidingEnvelope(stored, out var legacyEnvelope))
            return;

        if (!TryComputeRefreshTtl(legacyEnvelope.AbsoluteExpirationUtcTicks, legacyEnvelope.SlidingExpirationTicks, _timeProvider.GetUtcNow(), out var legacyRefreshTtl))
        {
            await _cache.RemoveAsync(cacheKey, ct).ConfigureAwait(false);
            return;
        }

        var migrated = CreateEnvelope(
            legacyEnvelope.Payload.Span,
            legacyEnvelope.AbsoluteExpirationUtcTicks,
            legacyEnvelope.SlidingExpirationTicks,
            isSliding: true);
        await _cache.SetAsync(cacheKey, migrated, CreateCacheEntryOptions(legacyRefreshTtl), ct).ConfigureAwait(false);
    }

    private ValueTask RemoveCoreAsync(string key, CancellationToken ct)
        => _cache.RemoveAsync(NormalizeKey(key), ct).AsValueTask();

    private ExpirationPlan BuildExpirationPlan(DistributedCacheEntryOptions options)
    {
        var now = _timeProvider.GetUtcNow();
        var slidingExpiration = options.SlidingExpiration;
        var relativeAbsoluteExpiration = options.AbsoluteExpirationRelativeToNow;

        if (slidingExpiration.HasValue && slidingExpiration.Value <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "SlidingExpiration must be greater than zero.");

        if (relativeAbsoluteExpiration.HasValue && relativeAbsoluteExpiration.Value <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "AbsoluteExpirationRelativeToNow must be greater than zero.");

        DateTimeOffset? absoluteExpiration = null;
        if (options.AbsoluteExpiration.HasValue)
            absoluteExpiration = options.AbsoluteExpiration.Value;

        if (relativeAbsoluteExpiration.HasValue)
        {
            var relativeAbsolute = now.Add(relativeAbsoluteExpiration.Value);
            absoluteExpiration = absoluteExpiration.HasValue && absoluteExpiration.Value < relativeAbsolute
                ? absoluteExpiration
                : relativeAbsolute;
        }

        if (absoluteExpiration.HasValue && absoluteExpiration.Value <= now)
            return ExpirationPlan.Remove;

        var absoluteTtl = absoluteExpiration.HasValue ? absoluteExpiration.Value - now : (TimeSpan?)null;
        var initialTtl = ChooseInitialTtl(absoluteTtl, slidingExpiration);
        if (initialTtl.HasValue && initialTtl.Value <= TimeSpan.Zero)
            return ExpirationPlan.Remove;

        return new ExpirationPlan(
            RemoveImmediately: false,
            RequiresSlidingEnvelope: slidingExpiration.HasValue,
            InitialTtl: initialTtl,
            AbsoluteExpirationUtcTicks: absoluteExpiration?.UtcDateTime.Ticks ?? 0,
            SlidingExpirationTicks: slidingExpiration?.Ticks ?? 0);
    }

    private string NormalizeKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return string.IsNullOrEmpty(_keyPrefix) ? key : string.Concat(_keyPrefix, key);
    }

    private static CacheEntryOptions CreateCacheEntryOptions(TimeSpan? ttl)
        => new(ttl, DistributedCacheIntent);

    private static TimeSpan? ChooseInitialTtl(TimeSpan? absoluteTtl, TimeSpan? slidingExpiration)
    {
        if (absoluteTtl is null)
            return slidingExpiration;

        if (slidingExpiration is null)
            return absoluteTtl;

        return absoluteTtl.Value <= slidingExpiration.Value ? absoluteTtl : slidingExpiration;
    }

    private static bool TryComputeRefreshTtl(
        long absoluteExpirationUtcTicks,
        long slidingExpirationTicks,
        DateTimeOffset now,
        out TimeSpan refreshTtl)
    {
        refreshTtl = default;
        if (slidingExpirationTicks <= 0)
            return false;

        var slidingExpiration = new TimeSpan(slidingExpirationTicks);
        if (absoluteExpirationUtcTicks <= 0)
        {
            refreshTtl = slidingExpiration;
            return refreshTtl > TimeSpan.Zero;
        }

        var absoluteExpiration = new DateTimeOffset(new DateTime(absoluteExpirationUtcTicks, DateTimeKind.Utc));
        var remaining = absoluteExpiration - now;
        if (remaining <= TimeSpan.Zero)
            return false;

        refreshTtl = remaining <= slidingExpiration ? remaining : slidingExpiration;
        return refreshTtl > TimeSpan.Zero;
    }

    private async ValueTask<CacheReadResult> ReadEnvelopeAsync(
        string cacheKey,
        CacheEnvelope envelope,
        bool refreshSliding,
        CancellationToken ct)
    {
        if (!envelope.IsSliding)
            return new CacheReadResult(envelope.Payload);

        var now = _timeProvider.GetUtcNow();
        if (!TryComputeRefreshTtl(envelope.AbsoluteExpirationUtcTicks, envelope.SlidingExpirationTicks, now, out var refreshTtl))
        {
            await _cache.RemoveAsync(cacheKey, ct).ConfigureAwait(false);
            return default;
        }

        if (refreshSliding)
        {
            var refreshed = CreateEnvelope(
                envelope.Payload.Span,
                envelope.AbsoluteExpirationUtcTicks,
                envelope.SlidingExpirationTicks,
                isSliding: true);
            await _cache.SetAsync(cacheKey, refreshed, CreateCacheEntryOptions(refreshTtl), ct).ConfigureAwait(false);
        }

        return new CacheReadResult(envelope.Payload);
    }

    private async ValueTask<CacheReadResult> ReadLegacySlidingEnvelopeAsync(
        string cacheKey,
        LegacySlidingEnvelope envelope,
        bool refreshSliding,
        CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow();
        if (!TryComputeRefreshTtl(envelope.AbsoluteExpirationUtcTicks, envelope.SlidingExpirationTicks, now, out var refreshTtl))
        {
            await _cache.RemoveAsync(cacheKey, ct).ConfigureAwait(false);
            return default;
        }

        if (refreshSliding)
        {
            var migrated = CreateEnvelope(
                envelope.Payload.Span,
                envelope.AbsoluteExpirationUtcTicks,
                envelope.SlidingExpirationTicks,
                isSliding: true);
            await _cache.SetAsync(cacheKey, migrated, CreateCacheEntryOptions(refreshTtl), ct).ConfigureAwait(false);
        }

        return new CacheReadResult(envelope.Payload);
    }

    private static bool HasEnvelopePrefix(byte[] payload)
    {
        var span = payload.AsSpan();
        return span.Length >= EnvelopePrefix.Length && span.StartsWith(EnvelopePrefix);
    }

    private static bool TryReadEnvelope(byte[] payload, out CacheEnvelope envelope)
    {
        envelope = default;
        var span = payload.AsSpan();
        if (span.Length < EnvelopePrefix.Length + sizeof(byte) + sizeof(long) + sizeof(long) + sizeof(int))
            return false;

        if (!span.StartsWith(EnvelopePrefix))
            return false;

        var offset = EnvelopePrefix.Length;
        if (span.Length - offset < sizeof(byte))
            return false;

        var flags = span[offset];
        offset += sizeof(byte);
        if (!TryReadInt64(span, ref offset, out var absoluteExpirationUtcTicks))
            return false;

        if (!TryReadInt64(span, ref offset, out var slidingExpirationTicks))
            return false;

        if (!TryReadInt32(span, ref offset, out var payloadLength) || payloadLength < 0)
            return false;

        if (span.Length - offset != payloadLength)
            return false;

        var isSliding = (flags & EnvelopeFlagSliding) != 0;
        if (!isSliding && slidingExpirationTicks != 0)
            return false;

        if (isSliding && slidingExpirationTicks <= 0)
            return false;

        envelope = new CacheEnvelope(
            IsSliding: isSliding,
            AbsoluteExpirationUtcTicks: absoluteExpirationUtcTicks,
            SlidingExpirationTicks: slidingExpirationTicks,
            Payload: payload.AsMemory(offset, payloadLength));
        return true;
    }

    private static bool TryReadLegacySlidingEnvelope(byte[] payload, out LegacySlidingEnvelope envelope)
    {
        envelope = default;
        var span = payload.AsSpan();
        if (span.Length < LegacySlidingEnvelopePrefix.Length + sizeof(long) + sizeof(long) + sizeof(int))
            return false;

        if (!span.StartsWith(LegacySlidingEnvelopePrefix))
            return false;

        var offset = LegacySlidingEnvelopePrefix.Length;
        if (!TryReadInt64(span, ref offset, out var absoluteExpirationUtcTicks))
            return false;

        if (!TryReadInt64(span, ref offset, out var slidingExpirationTicks) || slidingExpirationTicks <= 0)
            return false;

        if (!TryReadInt32(span, ref offset, out var payloadLength) || payloadLength < 0)
            return false;

        if (span.Length - offset != payloadLength)
            return false;

        envelope = new LegacySlidingEnvelope(
            AbsoluteExpirationUtcTicks: absoluteExpirationUtcTicks,
            SlidingExpirationTicks: slidingExpirationTicks,
            Payload: payload.AsMemory(offset, payloadLength));
        return true;
    }

    private static byte[] CreateEnvelope(
        ReadOnlySpan<byte> payload,
        long absoluteExpirationUtcTicks,
        long slidingExpirationTicks,
        bool isSliding)
    {
        var totalLength = EnvelopePrefix.Length + sizeof(byte) + sizeof(long) + sizeof(long) + sizeof(int) + payload.Length;
        var buffer = GC.AllocateUninitializedArray<byte>(totalLength);
        var span = buffer.AsSpan();
        var offset = 0;

        EnvelopePrefix.CopyTo(span);
        offset += EnvelopePrefix.Length;
        span[offset] = isSliding ? EnvelopeFlagSliding : (byte)0;
        offset += sizeof(byte);
        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(offset, sizeof(long)), absoluteExpirationUtcTicks);
        offset += sizeof(long);
        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(offset, sizeof(long)), slidingExpirationTicks);
        offset += sizeof(long);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset, sizeof(int)), payload.Length);
        offset += sizeof(int);
        payload.CopyTo(span.Slice(offset, payload.Length));
        return buffer;
    }

    private static bool TryReadInt32(ReadOnlySpan<byte> buffer, ref int offset, out int value)
    {
        value = 0;
        if (buffer.Length - offset < sizeof(int))
            return false;

        value = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(offset, sizeof(int)));
        offset += sizeof(int);
        return true;
    }

    private static bool TryReadInt64(ReadOnlySpan<byte> buffer, ref int offset, out long value)
    {
        value = 0;
        if (buffer.Length - offset < sizeof(long))
            return false;

        value = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset, sizeof(long)));
        offset += sizeof(long);
        return true;
    }

    private static void WaitSync(ValueTask task)
    {
        if (task.IsCompletedSuccessfully)
        {
            task.GetAwaiter().GetResult();
            return;
        }

        task.AsTask().GetAwaiter().GetResult();
    }

    private static T GetSync<T>(ValueTask<T> task)
    {
        if (task.IsCompletedSuccessfully)
            return task.Result;

        return task.AsTask().GetAwaiter().GetResult();
    }

    private readonly record struct ExpirationPlan(
        bool RemoveImmediately,
        bool RequiresSlidingEnvelope,
        TimeSpan? InitialTtl,
        long AbsoluteExpirationUtcTicks,
        long SlidingExpirationTicks)
    {
        public static ExpirationPlan Remove => new(
            RemoveImmediately: true,
            RequiresSlidingEnvelope: false,
            InitialTtl: null,
            AbsoluteExpirationUtcTicks: 0,
            SlidingExpirationTicks: 0);
    }

    private readonly record struct CacheEnvelope(
        bool IsSliding,
        long AbsoluteExpirationUtcTicks,
        long SlidingExpirationTicks,
        ReadOnlyMemory<byte> Payload);

    private readonly record struct LegacySlidingEnvelope(
        long AbsoluteExpirationUtcTicks,
        long SlidingExpirationTicks,
        ReadOnlyMemory<byte> Payload);

    private readonly struct CacheReadResult
    {
        public CacheReadResult(byte[] exactArray)
        {
            Value = exactArray;
            ExactArray = exactArray;
            HasValue = true;
        }

        public CacheReadResult(ReadOnlyMemory<byte> value)
        {
            Value = value;
            ExactArray = null;
            HasValue = true;
        }

        public bool HasValue { get; }
        public ReadOnlyMemory<byte> Value { get; }
        private byte[]? ExactArray { get; }

        public byte[] ToArray()
            => ExactArray ?? CopyBuffer(Value.Span);

        public void CopyTo(IBufferWriter<byte> destination)
        {
            var span = destination.GetSpan(Value.Length);
            Value.Span.CopyTo(span);
            destination.Advance(Value.Length);
        }

        private static byte[] CopyBuffer(ReadOnlySpan<byte> source)
        {
            if (source.IsEmpty)
                return Array.Empty<byte>();

            var buffer = GC.AllocateUninitializedArray<byte>(source.Length);
            source.CopyTo(buffer);
            return buffer;
        }
    }
}

internal static class ValueTaskExtensions
{
    public static ValueTask AsValueTask(this ValueTask<bool> task)
    {
        if (task.IsCompletedSuccessfully)
        {
            _ = task.Result;
            return ValueTask.CompletedTask;
        }

        return Await(task);

        static async ValueTask Await(ValueTask<bool> pending)
            => _ = await pending.ConfigureAwait(false);
    }
}
