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
    private static readonly ArraySegment<byte> EmptySegment = new(Array.Empty<byte>());

    private ManualResetValueTaskSourceCore<int> _core;
    private int _completed;
    private CancellationTokenRegistration _ctr;
    private ArraySegment<byte>[]? _currentBufferList;

    public SocketIoAwaitableEventArgs()
    {
        Completed += OnCompleted;
        _core.RunContinuationsAsynchronously = true;
    }

    /// <summary>
    /// Compatibility shim for benchmarks/tests that expect a <c>Reset()</c> method name.
    /// </summary>
    public void Reset() => ResetForOperation();

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

    [ThreadStatic] private static ArraySegment<byte>[]? _cachedSubset8;
    [ThreadStatic] private static ArraySegment<byte>[]? _cachedSubset16;
    [ThreadStatic] private static ArraySegment<byte>[]? _cachedSubset32;

    public void SetBufferList(ArraySegment<byte>[] buffers, int count)
    {
        if ((uint)count == 0 || count > buffers.Length)
            throw new ArgumentOutOfRangeException(nameof(count));

        BufferList = null;
        SetBuffer(null, 0, 0);

        ArraySegment<byte>[] subset;
        if (count <= 8)
        {
            subset = _cachedSubset8 ?? new ArraySegment<byte>[8];
            _cachedSubset8 = null;
        }
        else if (count <= 16)
        {
            subset = _cachedSubset16 ?? new ArraySegment<byte>[16];
            _cachedSubset16 = null;
        }
        else if (count <= 32)
        {
            subset = _cachedSubset32 ?? new ArraySegment<byte>[32];
            _cachedSubset32 = null;
        }
        else
        {
            subset = new ArraySegment<byte>[count];
        }

        Array.Copy(buffers, 0, subset, 0, count);
        for (var i = count; i < subset.Length; i++)
            subset[i] = EmptySegment;

        Volatile.Write(ref _currentBufferList, subset);
        BufferList = subset;
    }

    /// <summary>
    /// Return pooled buffer list when operation completes (call after socket operation finishes)
    /// </summary>
    public void ReturnBufferList()
    {
        var list = Interlocked.Exchange(ref _currentBufferList, null);
        if (list == null) return;

        // Now we own this list exclusively, safe to return to pool
        // Return to pool based on size
        if (list.Length == 8 && _cachedSubset8 == null)
        {
            Array.Clear(list, 0, list.Length); // Clear references
            _cachedSubset8 = list;
        }
        else if (list.Length == 16 && _cachedSubset16 == null)
        {
            Array.Clear(list, 0, list.Length);
            _cachedSubset16 = list;
        }
        else if (list.Length == 32 && _cachedSubset32 == null)
        {
            Array.Clear(list, 0, list.Length);
            _cachedSubset32 = list;
        }
        // Else: let GC collect non-standard sizes

        BufferList = null;
    }

    /// <summary>
    /// Compatibility shim for benchmarks/tests that expect <c>SetBuffer(ArraySegment&lt;byte&gt;[], int)</c>.
    /// (This is intentionally distinct from the base <see cref="SocketAsyncEventArgs.SetBuffer(byte[],int,int)"/>.)
    /// </summary>
    public void SetBuffer(ArraySegment<byte>[] buffers, int count) => SetBufferList(buffers, count);

    public ValueTask<int> WaitAsync() => new(this, _core.Version);

    public void RegisterCancellation(CancellationToken ct)
    {
        if (!ct.CanBeCanceled)
            return;

        // Store and dispose on completion/reset.
        _ctr = ct.Register(static state => ((SocketIoAwaitableEventArgs)state!).TrySetCanceled(), this);
    }

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
}
