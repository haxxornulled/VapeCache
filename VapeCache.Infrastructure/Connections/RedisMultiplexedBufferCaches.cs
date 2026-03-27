using System.Collections.Concurrent;
using System.Buffers;
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

    private static readonly ConcurrentBag<byte[]> SharedHeaderCache = new();
    private static readonly ConcurrentBag<byte[]> SharedSmallHeaderCache = new();
    private static readonly ConcurrentBag<ReadOnlyMemory<byte>[]> SharedPayloadArrayCache = new();
    private static readonly ConcurrentBag<ReadOnlyMemory<byte>[]> SharedSmallPayloadArrayCache = new();
    private static readonly ConcurrentDictionary<byte[], byte> InFlightHeaderBuffers = new();
    private static readonly ConcurrentDictionary<ReadOnlyMemory<byte>[], byte> InFlightPayloadArrays = new();

    private const int MaxSharedCacheSize = 64;
    private static int _sharedHeaderCount;
    private static int _sharedSmallHeaderCount;
    private static int _sharedPayloadArrayCount;
    private static int _sharedSmallPayloadArrayCount;

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
                InFlightHeaderBuffers.TryRemove(buf, out _);
                return buf;
            }

            if (TryTakeShared(SharedSmallHeaderCache, ref _sharedSmallHeaderCount, out var poolBuf) && poolBuf.Length >= minLength)
            {
                InFlightHeaderBuffers.TryRemove(poolBuf, out _);
                return poolBuf;
            }

            return new byte[512];
        }

        if (_tlsHeaderCache is { } largeBuf && largeBuf.Length >= minLength)
        {
            _tlsHeaderCache = null;
            InFlightHeaderBuffers.TryRemove(largeBuf, out _);
            return largeBuf;
        }

        while (TryTakeShared(SharedHeaderCache, ref _sharedHeaderCount, out var largePoolBuf))
        {
            if (largePoolBuf.Length >= minLength)
            {
                InFlightHeaderBuffers.TryRemove(largePoolBuf, out _);
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

        InFlightHeaderBuffers[buffer] = OwnershipInFlight;
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
                InFlightHeaderBuffers.TryRemove(buffer, out _);
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

        InFlightHeaderBuffers[buffer] = OwnershipReleasedByMux;
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

            if (TryAddShared(SharedSmallHeaderCache, ref _sharedSmallHeaderCount, buffer))
                return;
            return;
        }

        if (_tlsHeaderCache is null)
        {
            _tlsHeaderCache = buffer;
            return;
        }

        if (TryAddShared(SharedHeaderCache, ref _sharedHeaderCount, buffer))
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

        InFlightPayloadArrays[payloads] = OwnershipInFlight;
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
                InFlightPayloadArrays.TryRemove(arr, out _);
                return arr;
            }

            if (TryTakeShared(SharedSmallPayloadArrayCache, ref _sharedSmallPayloadArrayCount, out var poolArr) && poolArr.Length >= minLength)
            {
                InFlightPayloadArrays.TryRemove(poolArr, out _);
                return poolArr;
            }

            return new ReadOnlyMemory<byte>[16];
        }

        if (_tlsPayloadArrayCache is { } largeArr && largeArr.Length >= minLength)
        {
            _tlsPayloadArrayCache = null;
            InFlightPayloadArrays.TryRemove(largeArr, out _);
            return largeArr;
        }

        while (TryTakeShared(SharedPayloadArrayCache, ref _sharedPayloadArrayCount, out var largePoolArr))
        {
            if (largePoolArr.Length >= minLength)
            {
                InFlightPayloadArrays.TryRemove(largePoolArr, out _);
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
                InFlightPayloadArrays.TryRemove(payloads, out _);
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

        InFlightPayloadArrays[payloads] = OwnershipReleasedByMux;
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

            if (TryAddShared(SharedSmallPayloadArrayCache, ref _sharedSmallPayloadArrayCount, payloads))
                return;
            return;
        }

        if (_tlsPayloadArrayCache is null)
        {
            _tlsPayloadArrayCache = payloads;
            return;
        }

        if (TryAddShared(SharedPayloadArrayCache, ref _sharedPayloadArrayCount, payloads))
        {
            return;
        }

        ArrayPool<ReadOnlyMemory<byte>>.Shared.Return(payloads, clearArray: true);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryTakeShared<T>(ConcurrentBag<T> bag, ref int count, out T item)
    {
        if (bag.TryTake(out item!))
        {
            Interlocked.Decrement(ref count);
            return true;
        }

        item = default!;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryAddShared<T>(ConcurrentBag<T> bag, ref int count, T item)
    {
        while (true)
        {
            var snapshot = Volatile.Read(ref count);
            if (snapshot >= MaxSharedCacheSize)
                return false;

            if (Interlocked.CompareExchange(ref count, snapshot + 1, snapshot) != snapshot)
                continue;

            bag.Add(item);
            return true;
        }
    }
}
