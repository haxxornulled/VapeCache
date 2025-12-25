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
    private int _completed; // 0 = not completed, 1 = completed
    private CancellationTokenRegistration _ctr;

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
        // Ensure prior token registration is cleared.
        _ctr.Dispose();
        _ctr = default;

        Volatile.Write(ref _completed, 0);
        _core.Reset();

        // Clear any previous buffer state so we don't retain arrays.
        BufferList = null;
        SetBuffer(null, 0, 0);
    }

    public void SetBufferList(ArraySegment<byte>[] buffers, int count)
    {
        if ((uint)count == 0 || count > buffers.Length)
            throw new ArgumentOutOfRangeException(nameof(count));

        BufferList = null;
        SetBuffer(null, 0, 0);
        BufferList = buffers;
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
