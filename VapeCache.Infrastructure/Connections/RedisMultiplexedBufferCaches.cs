using System.Collections.Concurrent;
using System.Buffers;

namespace VapeCache.Infrastructure.Connections;

internal sealed class RedisMultiplexedBufferCaches
{
    [ThreadStatic] private static byte[]? _tlsHeaderCache;
    [ThreadStatic] private static byte[]? _tlsSmallHeaderCache;
    [ThreadStatic] private static ReadOnlyMemory<byte>[]? _tlsPayloadArrayCache;
    [ThreadStatic] private static ReadOnlyMemory<byte>[]? _tlsSmallPayloadArrayCache;

    private static readonly ConcurrentBag<byte[]> SharedHeaderCache = new();
    private static readonly ConcurrentBag<byte[]> SharedSmallHeaderCache = new();
    private static readonly ConcurrentBag<ReadOnlyMemory<byte>[]> SharedPayloadArrayCache = new();
    private static readonly ConcurrentBag<ReadOnlyMemory<byte>[]> SharedSmallPayloadArrayCache = new();

    private const int MaxSharedCacheSize = 64;

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
                return buf;
            }

            if (SharedSmallHeaderCache.TryTake(out var poolBuf) && poolBuf.Length >= minLength)
                return poolBuf;

            return new byte[512];
        }

        if (_tlsHeaderCache is { } largeBuf && largeBuf.Length >= minLength)
        {
            _tlsHeaderCache = null;
            return largeBuf;
        }

        if (SharedHeaderCache.TryTake(out var largePoolBuf) && largePoolBuf.Length >= minLength)
            return largePoolBuf;

        return ArrayPool<byte>.Shared.Rent(Math.Max(2048, minLength));
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public static void ReturnHeaderBuffer(byte[]? buffer)
    {
        if (buffer is null)
            return;

        if (buffer.Length <= 512)
        {
            if (_tlsSmallHeaderCache is null)
            {
                _tlsSmallHeaderCache = buffer;
                return;
            }

            if (SharedSmallHeaderCache.Count < MaxSharedCacheSize)
                SharedSmallHeaderCache.Add(buffer);
            return;
        }

        if (_tlsHeaderCache is null)
        {
            _tlsHeaderCache = buffer;
            return;
        }

        if (SharedHeaderCache.Count < MaxSharedCacheSize)
        {
            SharedHeaderCache.Add(buffer);
            return;
        }

        ArrayPool<byte>.Shared.Return(buffer);
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
                return arr;
            }

            if (SharedSmallPayloadArrayCache.TryTake(out var poolArr) && poolArr.Length >= minLength)
                return poolArr;

            return new ReadOnlyMemory<byte>[16];
        }

        if (_tlsPayloadArrayCache is { } largeArr && largeArr.Length >= minLength)
        {
            _tlsPayloadArrayCache = null;
            return largeArr;
        }

        if (SharedPayloadArrayCache.TryTake(out var largePoolArr) && largePoolArr.Length >= minLength)
            return largePoolArr;

        return ArrayPool<ReadOnlyMemory<byte>>.Shared.Rent(Math.Max(64, minLength));
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public static void ReturnPayloadArray(ReadOnlyMemory<byte>[]? payloads)
    {
        if (payloads is null)
            return;

        Array.Clear(payloads, 0, payloads.Length);
        if (payloads.Length <= 16)
        {
            if (_tlsSmallPayloadArrayCache is null)
            {
                _tlsSmallPayloadArrayCache = payloads;
                return;
            }

            if (SharedSmallPayloadArrayCache.Count < MaxSharedCacheSize)
                SharedSmallPayloadArrayCache.Add(payloads);
            return;
        }

        if (_tlsPayloadArrayCache is null)
        {
            _tlsPayloadArrayCache = payloads;
            return;
        }

        if (SharedPayloadArrayCache.Count < MaxSharedCacheSize)
        {
            SharedPayloadArrayCache.Add(payloads);
            return;
        }

        ArrayPool<ReadOnlyMemory<byte>>.Shared.Return(payloads, clearArray: true);
    }
}
