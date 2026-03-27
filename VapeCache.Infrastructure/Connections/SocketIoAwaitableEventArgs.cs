using System.Collections;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace VapeCache.Infrastructure.Connections;

/// <summary>
/// Pooled SocketAsyncEventArgs that exposes completion as a ValueTask{int} without per-operation allocations.
/// Correctly handles synchronous completion, SocketError propagation, and cancel/complete races.
/// Intended for single-operation-at-a-time usage by a single loop (writer or reader).
/// </summary>
internal sealed class SocketIoAwaitableEventArgs : SocketAsyncEventArgs, IValueTaskSource<int>
{
    private ManualResetValueTaskSourceCore<int> _core;
    private int _completed;
    private CancellationTokenRegistration _ctr;
    private readonly BufferListWindow _bufferListWindow = new();

    public SocketIoAwaitableEventArgs()
    {
        Completed += OnCompleted;
        _core.RunContinuationsAsynchronously = true;
    }

    /// <summary>
    /// Compatibility shim for benchmarks/tests that expect a <c>Reset()</c> method name.
    /// </summary>
    public void Reset() => ResetForOperation();

    /// <summary>
    /// Executes value.
    /// </summary>
    public void ResetForOperation()
    {
        _ctr.Dispose();
        _ctr = default;

        Volatile.Write(ref _completed, 0);
        _core.Reset();

        ReturnBufferList();

        BufferList = null;
        SetBuffer(Array.Empty<byte>(), 0, 0);
    }

    /// <summary>
    /// Sets value.
    /// </summary>
    public void SetBufferList(ArraySegment<byte>[] buffers, int count)
        => SetBufferList(buffers, 0, count);

    /// <summary>
    /// Sets value.
    /// </summary>
    public void SetBufferList(ArraySegment<byte>[] buffers, int offset, int count)
    {
        if ((uint)count == 0 || offset < 0 || count > buffers.Length - offset)
            throw new ArgumentOutOfRangeException(nameof(count));

        BufferList = null;
        SetBuffer(null, 0, 0);
        _bufferListWindow.Reset(buffers, offset, count);
        BufferList = _bufferListWindow;
    }

    /// <summary>
    /// Return pooled buffer list when operation completes (call after socket operation finishes)
    /// </summary>
    public void ReturnBufferList()
    {
        _bufferListWindow.Clear();
        BufferList = null;
    }

    /// <summary>
    /// Compatibility shim for benchmarks/tests that expect <c>SetBuffer(ArraySegment&lt;byte&gt;[], int)</c>.
    /// (This is intentionally distinct from the base <see cref="SocketAsyncEventArgs.SetBuffer(byte[],int,int)"/>.)
    /// </summary>
    public void SetBuffer(ArraySegment<byte>[] buffers, int count) => SetBufferList(buffers, count);

    /// <summary>
    /// Executes value.
    /// </summary>
    public ValueTask<int> WaitAsync() => new(this, _core.Version);

    /// <summary>
    /// Executes value.
    /// </summary>
    public void RegisterCancellation(CancellationToken ct)
    {
        if (!ct.CanBeCanceled)
            return;

        // Store and dispose on completion/reset.
        _ctr = ct.Register(static state => ((SocketIoAwaitableEventArgs)state!).TrySetCanceled(), this);
    }

    /// <summary>
    /// Attempts to value.
    /// </summary>
    public void TrySetCanceled()
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
            return;

        // Ensure we don't keep token registrations alive.
        _ctr.Dispose();
        _ctr = default;

        _core.SetException(new OperationCanceledException());
    }

    private void OnCompleted(object? sender, SocketAsyncEventArgs e)
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
            return;

        _ctr.Dispose();
        _ctr = default;

        if (SocketError == SocketError.Success)
            _core.SetResult(BytesTransferred);
        else
            _core.SetException(new SocketException((int)SocketError));
    }

    /// <summary>
    /// Test/benchmark helper: resets internal state and returns the awaitable for the next completion.
    /// </summary>
    internal ValueTask<int> BeginForTests()
    {
        ResetForOperation();
        return WaitAsync();
    }

    /// <summary>
    /// Test/benchmark helper: completes the current operation without touching the underlying socket.
    /// </summary>
    internal void CompleteForTests(int bytes, SocketError error = SocketError.Success)
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
            return;

        _ctr.Dispose();
        _ctr = default;

        if (error == SocketError.Success)
            _core.SetResult(bytes);
        else
            _core.SetException(new SocketException((int)error));
    }

    /// <summary>
    /// Gets value.
    /// </summary>
    public int GetResult(short token) => _core.GetResult(token);

    ValueTaskSourceStatus IValueTaskSource<int>.GetStatus(short token) => _core.GetStatus(token);

    void IValueTaskSource<int>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        => _core.OnCompleted(continuation, state, token, flags);

    /// <summary>
    /// Completes the current operation inline (used for synchronous completion paths).
    /// </summary>
    public int CompleteInlineOrThrow()
    {
        // Mark completed so a late Completed event (should not happen for sync completion) won't double-complete.
        if (Interlocked.Exchange(ref _completed, 1) != 0)
            return BytesTransferred;

        _ctr.Dispose();
        _ctr = default;

        if (SocketError != SocketError.Success)
            throw new SocketException((int)SocketError);

        return BytesTransferred;
    }

    private sealed class BufferListWindow : IList<ArraySegment<byte>>
    {
        private static readonly ArraySegment<byte>[] Empty = Array.Empty<ArraySegment<byte>>();

        private ArraySegment<byte>[] _source = Empty;
        private int _offset;
        private int _count;

        public int Count => _count;
        public bool IsReadOnly => true;

        public ArraySegment<byte> this[int index]
        {
            get
            {
                if ((uint)index >= (uint)_count)
                    throw new ArgumentOutOfRangeException(nameof(index));

                return _source[_offset + index];
            }
            set => throw new NotSupportedException();
        }

        public void Reset(ArraySegment<byte>[] source, int offset, int count)
        {
            _source = source;
            _offset = offset;
            _count = count;
        }

        public void Clear()
        {
            _source = Empty;
            _offset = 0;
            _count = 0;
        }

        public int IndexOf(ArraySegment<byte> item)
        {
            for (var i = 0; i < _count; i++)
            {
                if (_source[_offset + i].Equals(item))
                    return i;
            }

            return -1;
        }

        public bool Contains(ArraySegment<byte> item) => IndexOf(item) >= 0;

        public void CopyTo(ArraySegment<byte>[] array, int arrayIndex)
        {
            ArgumentNullException.ThrowIfNull(array);
            if (arrayIndex < 0 || arrayIndex > array.Length)
                throw new ArgumentOutOfRangeException(nameof(arrayIndex));
            if (array.Length - arrayIndex < _count)
                throw new ArgumentException("Destination array is too small.", nameof(array));

            Array.Copy(_source, _offset, array, arrayIndex, _count);
        }

        public IEnumerator<ArraySegment<byte>> GetEnumerator()
        {
            for (var i = 0; i < _count; i++)
                yield return _source[_offset + i];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public void Add(ArraySegment<byte> item) => throw new NotSupportedException();
        public void Insert(int index, ArraySegment<byte> item) => throw new NotSupportedException();
        public bool Remove(ArraySegment<byte> item) => throw new NotSupportedException();
        public void RemoveAt(int index) => throw new NotSupportedException();
        void ICollection<ArraySegment<byte>>.Clear() => throw new NotSupportedException();
    }
}
