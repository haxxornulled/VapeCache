using System.Buffers;
using System.Collections.Concurrent;
using System.Threading;

namespace VapeCache.Abstractions.Connections;

/// <summary>
/// Represents a leased Redis value buffer.
/// </summary>
public sealed class RedisValueLease : IDisposable
{
    private static readonly ConcurrentBag<RedisValueLease> LeasePool = new();
    private static readonly RedisValueLease NullInstance = new(isNullLease: true);

    private byte[]? _buffer;
    private int _length;
    private bool _pooledBuffer;
    private readonly bool _isNullLease;
    private int _disposed; // 0 = not disposed, 1 = disposed

    private RedisValueLease(bool isNullLease = false)
    {
        _isNullLease = isNullLease;
        _disposed = isNullLease ? 1 : 0;
    }

    internal static RedisValueLease Create(byte[]? buffer, int length, bool pooled)
    {
        if (buffer is null || length <= 0)
            return Null;

        if (!LeasePool.TryTake(out var lease))
            lease = new RedisValueLease();

        lease._buffer = buffer;
        lease._length = length;
        lease._pooledBuffer = pooled;
        Volatile.Write(ref lease._disposed, 0);
        return lease;
    }

    /// <summary>
    /// Defines the null.
    /// </summary>
    public static RedisValueLease Null => NullInstance;

    /// <summary>
    /// Executes read.
    /// </summary>
    public bool IsNull => Volatile.Read(ref _disposed) != 0 || _buffer is null;
    /// <summary>
    /// Executes read.
    /// </summary>
    public int Length => Volatile.Read(ref _disposed) != 0 || _buffer is null ? 0 : _length;

    /// <summary>
    /// Executes read.
    /// </summary>
    public ReadOnlyMemory<byte> Memory => Volatile.Read(ref _disposed) != 0 || _buffer is null ? ReadOnlyMemory<byte>.Empty : _buffer.AsMemory(0, _length);
    /// <summary>
    /// Executes read.
    /// </summary>
    public ReadOnlySpan<byte> Span => Volatile.Read(ref _disposed) != 0 || _buffer is null ? ReadOnlySpan<byte>.Empty : _buffer.AsSpan(0, _length);

    /// <summary>
    /// Releases resources used by the current instance.
    /// </summary>
    public void Dispose()
    {
        if (_isNullLease)
            return;

        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        var buffer = _buffer;
        var pooled = _pooledBuffer;
        _buffer = null;
        _length = 0;
        _pooledBuffer = false;

        if (pooled && buffer is not null)
            ArrayPool<byte>.Shared.Return(buffer);

        LeasePool.Add(this);
    }
}
