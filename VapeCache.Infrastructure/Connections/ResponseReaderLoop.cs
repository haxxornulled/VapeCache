using System.Net.Sockets;
using System.IO;

namespace VapeCache.Infrastructure.Connections;

internal sealed class ResponseReaderLoop
{
    private readonly bool _useSocketReader;
    private readonly TimeSpan _responseTimeout;
    private readonly CancellationToken _shutdownToken;
    private readonly Func<RedisRespSocketReaderState?> _socketReaderAccessor;
    private readonly Func<RedisRespReaderState?> _streamReaderAccessor;
    private readonly Func<SocketError, bool> _isFatalSocket;
    private readonly Func<Exception, Task> _failTransportAsync;

    public ResponseReaderLoop(
        bool useSocketReader,
        TimeSpan responseTimeout,
        CancellationToken shutdownToken,
        Func<RedisRespSocketReaderState?> socketReaderAccessor,
        Func<RedisRespReaderState?> streamReaderAccessor,
        Func<SocketError, bool> isFatalSocket,
        Func<Exception, Task> failTransportAsync)
    {
        _useSocketReader = useSocketReader;
        _responseTimeout = responseTimeout;
        _shutdownToken = shutdownToken;
        _socketReaderAccessor = socketReaderAccessor;
        _streamReaderAccessor = streamReaderAccessor;
        _isFatalSocket = isFatalSocket;
        _failTransportAsync = failTransportAsync;
    }

    public async Task<(RedisRespReader.RespValue? Response, Exception? Error)> ReadAsync(bool poolBulk)
    {
        CancellationTokenSource? timeoutCts = null;
        var readToken = _shutdownToken;
        if (_responseTimeout > TimeSpan.Zero)
        {
            timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownToken);
            timeoutCts.CancelAfter(_responseTimeout);
            readToken = timeoutCts.Token;
        }

        try
        {
            if (_useSocketReader)
            {
                var reader = _socketReaderAccessor() ?? throw new InvalidOperationException("RESP socket reader missing after connection established.");
                return await ReadCoreAsync(
                    () => reader.ReadAsync(poolBulk, readToken),
                    readToken,
                    ioeFatalMap: static _ => null).ConfigureAwait(false);
            }

            var streamReader = _streamReaderAccessor() ?? throw new InvalidOperationException("RESP reader missing after connection established.");
            return await ReadCoreAsync(
                () => streamReader.ReadAsync(poolBulk, readToken),
                readToken,
                ioeFatalMap: ex => ex.InnerException is SocketException se && _isFatalSocket(se.SocketErrorCode) ? ex : null).ConfigureAwait(false);
        }
        finally
        {
            timeoutCts?.Dispose();
        }
    }

    private async Task<(RedisRespReader.RespValue? Response, Exception? Error)> ReadCoreAsync(
        Func<ValueTask<RedisRespReader.RespValue>> read,
        CancellationToken readToken,
        Func<IOException, Exception?> ioeFatalMap)
    {
        try
        {
            while (true)
            {
                var response = await read().ConfigureAwait(false);
                if (response.Kind == RedisRespReader.RespKind.Push)
                {
                    RedisRespReader.ReturnBuffers(response);
                    continue;
                }

                if (response.Kind == RedisRespReader.RespKind.Error)
                    return (response, new InvalidOperationException(response.Text ?? "Redis error"));
                return (response, null);
            }
        }
        catch (OperationCanceledException) when (_shutdownToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException oce)
        {
            var timeout = new TimeoutException($"Redis response timed out after {_responseTimeout}.", oce);
            return (null, timeout);
        }
        catch (IOException ioe) when (ioeFatalMap(ioe) is { } fatalIo)
        {
            await _failTransportAsync(fatalIo).ConfigureAwait(false);
            return (null, fatalIo);
        }
        catch (SocketException se) when (_isFatalSocket(se.SocketErrorCode))
        {
            await _failTransportAsync(se).ConfigureAwait(false);
            return (null, se);
        }
        catch (Exception ex)
        {
            return (null, ex);
        }
    }
}
