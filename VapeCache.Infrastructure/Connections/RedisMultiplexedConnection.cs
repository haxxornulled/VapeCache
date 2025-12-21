using System.Threading.Channels;
using VapeCache.Abstractions.Connections;

namespace VapeCache.Infrastructure.Connections;

internal sealed class RedisMultiplexedConnection : IAsyncDisposable
{
    private readonly IRedisConnectionFactory _factory;
    private readonly int _maxInFlight;

    private readonly Channel<PendingRequest> _writes;
    private readonly Channel<TaskCompletionSource<RedisRespReader.RespValue>> _pending;
    private readonly SemaphoreSlim _inFlight;

    private readonly CancellationTokenSource _cts = new();
    private readonly Task _writer;
    private readonly Task _reader;

    private IRedisConnection? _conn;
    private int _disposed;

    public RedisMultiplexedConnection(IRedisConnectionFactory factory, int maxInFlight)
    {
        _factory = factory;
        _maxInFlight = Math.Max(1, maxInFlight);
        _inFlight = new SemaphoreSlim(_maxInFlight, _maxInFlight);

        _writes = Channel.CreateUnbounded<PendingRequest>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = true
        });

        _pending = Channel.CreateUnbounded<TaskCompletionSource<RedisRespReader.RespValue>>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = true
        });

        _writer = Task.Run(WriterLoopAsync);
        _reader = Task.Run(ReaderLoopAsync);
    }

    public async ValueTask<RedisRespReader.RespValue> ExecuteAsync(ReadOnlyMemory<byte> command, CancellationToken ct)
    {
        if (Volatile.Read(ref _disposed) == 1) throw new ObjectDisposedException(nameof(RedisMultiplexedConnection));

        await _inFlight.WaitAsync(ct).ConfigureAwait(false);

        var tcs = new TaskCompletionSource<RedisRespReader.RespValue>(TaskCreationOptions.RunContinuationsAsynchronously);
        var req = new PendingRequest(command, tcs);

        try
        {
            await _writes.Writer.WriteAsync(req, ct).ConfigureAwait(false);
            return await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _inFlight.Release();
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_conn is not null) return;
        var created = await _factory.CreateAsync(ct).ConfigureAwait(false);
        _conn = created.Match(static c => c, static ex => throw ex);
    }

    private async Task WriterLoopAsync()
    {
        try
        {
            while (await _writes.Reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
            {
                while (_writes.Reader.TryRead(out var req))
                {
                    await EnsureConnectedAsync(_cts.Token).ConfigureAwait(false);
                    var conn = _conn!;

                    // Enqueue response waiter before writing to preserve ordering.
                    await _pending.Writer.WriteAsync(req.Tcs, _cts.Token).ConfigureAwait(false);

                    await conn.Stream.WriteAsync(req.Command, _cts.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            FaultAll(ex);
        }
    }

    private async Task ReaderLoopAsync()
    {
        try
        {
            while (await _pending.Reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
            {
                while (_pending.Reader.TryRead(out var next))
                {
                    await EnsureConnectedAsync(_cts.Token).ConfigureAwait(false);
                    var resp = await RedisRespReader.ReadAsync(_conn!.Stream, _cts.Token).ConfigureAwait(false);

                    if (resp.Kind == RedisRespReader.RespKind.Error)
                        next.TrySetException(new InvalidOperationException(resp.Text ?? "Redis error"));
                    else
                        next.TrySetResult(resp);
                }
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            FaultAll(ex);
        }
    }

    private void FaultAll(Exception ex)
    {
        try { _writes.Writer.TryComplete(ex); } catch { }
        try { _pending.Writer.TryComplete(ex); } catch { }

        while (_pending.Reader.TryRead(out var t))
            t.TrySetException(ex);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        try { _cts.Cancel(); } catch { }
        _writes.Writer.TryComplete();
        _pending.Writer.TryComplete();

        try { await _writer.ConfigureAwait(false); } catch { }
        try { await _reader.ConfigureAwait(false); } catch { }

        _cts.Dispose();
        _inFlight.Dispose();

        if (_conn is not null)
            await _conn.DisposeAsync().ConfigureAwait(false);
    }

    private readonly record struct PendingRequest(ReadOnlyMemory<byte> Command, TaskCompletionSource<RedisRespReader.RespValue> Tcs);
}
