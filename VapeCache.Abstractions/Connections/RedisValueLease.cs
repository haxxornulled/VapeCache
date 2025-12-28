using System.Buffers;

namespace VapeCache.Abstractions.Connections;

// CRITICAL FIX P1-3: Add disposal tracking to prevent double-dispose bug
// Cannot use 'ref struct' because it's incompatible with ValueTask<T>
// Instead use disposal flag - small overhead (4 bytes) but prevents buffer pool corruption
public struct RedisValueLease : IDisposable
{
    private readonly byte[]? _buffer;
    private readonly int _length;
    private readonly bool _pooled;
    private int _disposed; // 0 = not disposed, 1 = disposed

    internal RedisValueLease(byte[] buffer, int length, bool pooled)
    {
        _buffer = buffer;
        _length = length;
        _pooled = pooled;
        _disposed = 0;
    }

    public static RedisValueLease Null => default;

    public bool IsNull => _buffer is null;
    public int Length => _buffer is null ? 0 : _length;

    public ReadOnlyMemory<byte> Memory => _buffer is null ? ReadOnlyMemory<byte>.Empty : _buffer.AsMemory(0, _length);
    public ReadOnlySpan<byte> Span => _buffer is null ? ReadOnlySpan<byte>.Empty : _buffer.AsSpan(0, _length);

    public void Dispose()
    {
        // CRITICAL FIX P1-3: Atomic check-and-set to prevent double-dispose
        // Only first thread to dispose will return buffer to pool
        if (_pooled && _buffer is not null)
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                ArrayPool<byte>.Shared.Return(_buffer);
        }
    }
}

