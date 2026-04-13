using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace VapeCache.Infrastructure.Connections;

internal sealed class RedisMultiplexedBufferCaches
{
    private const byte OwnershipInFlight = 1;
    private const byte OwnershipReleasedByMux = 2;

    [ThreadStatic] private static byte[]? _tlsHeaderCache;
    [ThreadStatic] private static byte[]? _tlsSmallHeaderCache;
    [ThreadStatic] private static ReadOnlyMemory<byte>[]? _tlsPayloadArrayCache;
    [ThreadStatic] private static ReadOnlyMemory<byte>[]? _tlsSmallPayloadArrayCache;

    private const int MaxSharedCacheSize = 64;
    private static readonly SharedBufferCache<byte[]> SharedHeaderCache = new(MaxSharedCacheSize);
    private static readonly SharedBufferCache<byte[]> SharedSmallHeaderCache = new(MaxSharedCacheSize);
    private static readonly SharedBufferCache<ReadOnlyMemory<byte>[]> SharedPayloadArrayCache = new(MaxSharedCacheSize);
    private static readonly SharedBufferCache<ReadOnlyMemory<byte>[]> SharedSmallPayloadArrayCache = new(MaxSharedCacheSize);
    private static readonly OwnershipTable<byte[]> InFlightHeaderBuffers = new();
    private static readonly OwnershipTable<ReadOnlyMemory<byte>[]> InFlightPayloadArrays = new();

    /// <summary>
    /// Executes value.
    /// </summary>
    public static byte[] RentHeaderBuffer(int minLength)
    {
        if (minLength <= 512)
        {
            if (_tlsSmallHeaderCache is { } buf && buf.Length >= minLength)
            {
                _tlsSmallHeaderCache = null;
                InFlightHeaderBuffers.Remove(buf);
                return buf;
            }

            if (SharedSmallHeaderCache.TryTake(out var poolBuf) && poolBuf.Length >= minLength)
            {
                InFlightHeaderBuffers.Remove(poolBuf);
                return poolBuf;
            }

            return new byte[512];
        }

        if (_tlsHeaderCache is { } largeBuf && largeBuf.Length >= minLength)
        {
            _tlsHeaderCache = null;
            InFlightHeaderBuffers.Remove(largeBuf);
            return largeBuf;
        }

        while (SharedHeaderCache.TryTake(out var largePoolBuf))
        {
            if (largePoolBuf.Length >= minLength)
            {
                InFlightHeaderBuffers.Remove(largePoolBuf);
                return largePoolBuf;
            }

            ArrayPool<byte>.Shared.Return(largePoolBuf);
        }

        return ArrayPool<byte>.Shared.Rent(Math.Max(2048, minLength));
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void MarkHeaderBufferInFlight(byte[]? buffer)
    {
        if (buffer is null)
            return;

        InFlightHeaderBuffers.Set(buffer, OwnershipInFlight);
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public static void ReturnHeaderBuffer(byte[]? buffer) => ReturnHeaderBufferFromCaller(buffer);

    /// <summary>
    /// Executes value.
    /// </summary>
    public static void ReturnHeaderBufferFromCaller(byte[]? buffer)
    {
        if (buffer is null)
            return;

        if (InFlightHeaderBuffers.TryGetValue(buffer, out var ownership))
        {
            if (ownership == OwnershipReleasedByMux)
                InFlightHeaderBuffers.Remove(buffer);
            return;
        }

        ReturnHeaderBufferCore(buffer);
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public static void ReturnHeaderBufferFromMux(byte[]? buffer)
    {
        if (buffer is null)
            return;

        InFlightHeaderBuffers.Set(buffer, OwnershipReleasedByMux);
        ReturnHeaderBufferCore(buffer);
    }

    private static void ReturnHeaderBufferCore(byte[] buffer)
    {
        if (buffer.Length <= 512)
        {
            if (_tlsSmallHeaderCache is null)
            {
                _tlsSmallHeaderCache = buffer;
                return;
            }

            if (SharedSmallHeaderCache.TryAdd(buffer))
                return;
            return;
        }

        if (_tlsHeaderCache is null)
        {
            _tlsHeaderCache = buffer;
            return;
        }

        if (SharedHeaderCache.TryAdd(buffer))
        {
            return;
        }

        ArrayPool<byte>.Shared.Return(buffer);
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void MarkPayloadArrayInFlight(ReadOnlyMemory<byte>[]? payloads)
    {
        if (payloads is null)
            return;

        InFlightPayloadArrays.Set(payloads, OwnershipInFlight);
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public static ReadOnlyMemory<byte>[] RentPayloadArray(int minLength)
    {
        if (minLength <= 16)
        {
            if (_tlsSmallPayloadArrayCache is { } arr && arr.Length >= minLength)
            {
                _tlsSmallPayloadArrayCache = null;
                InFlightPayloadArrays.Remove(arr);
                return arr;
            }

            if (SharedSmallPayloadArrayCache.TryTake(out var poolArr) && poolArr.Length >= minLength)
            {
                InFlightPayloadArrays.Remove(poolArr);
                return poolArr;
            }

            return new ReadOnlyMemory<byte>[16];
        }

        if (_tlsPayloadArrayCache is { } largeArr && largeArr.Length >= minLength)
        {
            _tlsPayloadArrayCache = null;
            InFlightPayloadArrays.Remove(largeArr);
            return largeArr;
        }

        while (SharedPayloadArrayCache.TryTake(out var largePoolArr))
        {
            if (largePoolArr.Length >= minLength)
            {
                InFlightPayloadArrays.Remove(largePoolArr);
                return largePoolArr;
            }

            ArrayPool<ReadOnlyMemory<byte>>.Shared.Return(largePoolArr, clearArray: true);
        }

        return ArrayPool<ReadOnlyMemory<byte>>.Shared.Rent(Math.Max(64, minLength));
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public static void ReturnPayloadArray(ReadOnlyMemory<byte>[]? payloads) => ReturnPayloadArrayFromCaller(payloads);

    /// <summary>
    /// Executes value.
    /// </summary>
    public static void ReturnPayloadArrayFromCaller(ReadOnlyMemory<byte>[]? payloads)
    {
        if (payloads is null)
            return;

        if (InFlightPayloadArrays.TryGetValue(payloads, out var ownership))
        {
            if (ownership == OwnershipReleasedByMux)
                InFlightPayloadArrays.Remove(payloads);
            return;
        }

        ReturnPayloadArrayCore(payloads);
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public static void ReturnPayloadArrayFromMux(ReadOnlyMemory<byte>[]? payloads)
    {
        if (payloads is null)
            return;

        InFlightPayloadArrays.Set(payloads, OwnershipReleasedByMux);
        ReturnPayloadArrayCore(payloads);
    }

    private static void ReturnPayloadArrayCore(ReadOnlyMemory<byte>[] payloads)
    {
        Array.Clear(payloads, 0, payloads.Length);
        if (payloads.Length <= 16)
        {
            if (_tlsSmallPayloadArrayCache is null)
            {
                _tlsSmallPayloadArrayCache = payloads;
                return;
            }

            if (SharedSmallPayloadArrayCache.TryAdd(payloads))
                return;
            return;
        }

        if (_tlsPayloadArrayCache is null)
        {
            _tlsPayloadArrayCache = payloads;
            return;
        }

        if (SharedPayloadArrayCache.TryAdd(payloads))
        {
            return;
        }

        ArrayPool<ReadOnlyMemory<byte>>.Shared.Return(payloads, clearArray: true);
    }

    private sealed class SharedBufferCache<T> where T : class
    {
        private readonly System.Threading.Lock _gate = new();
        private readonly T?[] _items;
        private int _count;

        public SharedBufferCache(int capacity)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
            _items = new T[capacity];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryTake(out T item)
        {
            lock (_gate)
            {
                if (_count == 0)
                {
                    item = default!;
                    return false;
                }

                var next = --_count;
                item = _items[next]!;
                _items[next] = default;
                return true;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryAdd(T item)
        {
            lock (_gate)
            {
                if (_count == _items.Length)
                    return false;

                _items[_count++] = item;
                return true;
            }
        }
    }

    private sealed class OwnershipTable<T> where T : class
    {
        private const int StripeCount = 32;
        private readonly Stripe[] _stripes;

        public OwnershipTable()
        {
            _stripes = new Stripe[StripeCount];
            for (var i = 0; i < _stripes.Length; i++)
                _stripes[i] = new Stripe();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Set(T item, byte state)
        {
            var stripe = GetStripe(item);
            lock (stripe.Gate)
            {
                stripe.Map[item] = state;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetValue(T item, out byte state)
        {
            var stripe = GetStripe(item);
            lock (stripe.Gate)
            {
                return stripe.Map.TryGetValue(item, out state);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Remove(T item)
        {
            var stripe = GetStripe(item);
            lock (stripe.Gate)
            {
                stripe.Map.Remove(item);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Stripe GetStripe(T item)
            => _stripes[RuntimeHelpers.GetHashCode(item) & (StripeCount - 1)];

        private sealed class Stripe
        {
            public readonly System.Threading.Lock Gate = new();
            public readonly Dictionary<T, byte> Map = new(ReferenceEqualityComparer.Instance);
        }
    }
}
