using System.Buffers;

namespace VapeCache.Infrastructure.Caching;

internal sealed class PooledByteBufferWriter : IBufferWriter<byte>, IDisposable
{
    private byte[] _buffer;
    private int _written;
    private bool _disposed;

    public PooledByteBufferWriter(int initialCapacity = 256)
    {
        _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(1, initialCapacity));
    }

    public ReadOnlyMemory<byte> WrittenMemory
        => _disposed ? ReadOnlyMemory<byte>.Empty : _buffer.AsMemory(0, _written);

    public ReadOnlySpan<byte> WrittenSpan
        => _disposed ? ReadOnlySpan<byte>.Empty : _buffer.AsSpan(0, _written);

    public int WrittenCount => _disposed ? 0 : _written;

    public void Advance(int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((uint)count > (uint)(_buffer.Length - _written))
            throw new ArgumentOutOfRangeException(nameof(count));

        _written += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsMemory(_written);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsSpan(_written);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        var buffer = _buffer;
        _buffer = Array.Empty<byte>();
        _written = 0;
        if (buffer.Length > 0)
            ArrayPool<byte>.Shared.Return(buffer);
    }

    private void EnsureCapacity(int sizeHint)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);

        if (sizeHint == 0)
            sizeHint = 1;

        var available = _buffer.Length - _written;
        if (available >= sizeHint)
            return;

        var required = _written + sizeHint;
        var newSize = Math.Max(required, _buffer.Length * 2);
        var newBuffer = ArrayPool<byte>.Shared.Rent(newSize);
        _buffer.AsSpan(0, _written).CopyTo(newBuffer);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = newBuffer;
    }
}
