using System.Buffers;

namespace VapeCache.Abstractions.Connections;

public readonly struct RedisValueLease : IDisposable
{
    private readonly byte[]? _buffer;
    private readonly int _length;
    private readonly bool _pooled;

    internal RedisValueLease(byte[] buffer, int length, bool pooled)
    {
        _buffer = buffer;
        _length = length;
        _pooled = pooled;
    }

    public static RedisValueLease Null => default;

    public bool IsNull => _buffer is null;
    public int Length => _buffer is null ? 0 : _length;

    public ReadOnlyMemory<byte> Memory => _buffer is null ? ReadOnlyMemory<byte>.Empty : _buffer.AsMemory(0, _length);
    public ReadOnlySpan<byte> Span => _buffer is null ? ReadOnlySpan<byte>.Empty : _buffer.AsSpan(0, _length);

    public void Dispose()
    {
        if (_pooled && _buffer is not null)
            ArrayPool<byte>.Shared.Return(_buffer);
    }
}

