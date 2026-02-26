using System.Buffers;

namespace VapeCache.Abstractions.Connections;

/// <summary>
/// Represents a leased Redis value buffer.
/// Reference type by design so disposal state cannot be bypassed via struct copies.
/// </summary>
public sealed class RedisValueLease : IDisposable
{
    private static readonly RedisValueLease NullInstance = new(buffer: null, length: 0, pooled: false);

    private readonly byte[]? _buffer;
    private readonly int _length;
    private readonly bool _pooled;
    private int _disposed; // 0 = not disposed, 1 = disposed

    internal RedisValueLease(byte[]? buffer, int length, bool pooled)
    {
        _buffer = buffer;
        _length = length;
        _pooled = pooled;
        _disposed = 0;
    }

    public static RedisValueLease Null => NullInstance;

    public bool IsNull => _buffer is null;
    public int Length => _buffer is null ? 0 : _length;

    public ReadOnlyMemory<byte> Memory => _buffer is null ? ReadOnlyMemory<byte>.Empty : _buffer.AsMemory(0, _length);
    public ReadOnlySpan<byte> Span => _buffer is null ? ReadOnlySpan<byte>.Empty : _buffer.AsSpan(0, _length);

    /// <summary>
    /// Releases resources used by the current instance.
    /// </summary>
    public void Dispose()
    {
        if (_pooled && _buffer is not null)
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                ArrayPool<byte>.Shared.Return(_buffer);
        }
    }
}
