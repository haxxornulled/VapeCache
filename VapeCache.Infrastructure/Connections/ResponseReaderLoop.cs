using System.Net.Sockets;
using System.IO;
using System.Runtime.CompilerServices;

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

    public ValueTask<ResponseReadResult> ReadAsync(bool poolBulk, RedisResponseMode responseMode)
    {
        if (_responseTimeout <= TimeSpan.Zero)
        {
            return _useSocketReader
                ? ReadSocketAsync(poolBulk, responseMode, _shutdownToken)
                : ReadStreamAsync(poolBulk, responseMode, _shutdownToken);
        }

        return ReadWithTimeoutAsync(poolBulk, responseMode);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<ResponseReadResult> ReadWithTimeoutAsync(bool poolBulk, RedisResponseMode responseMode)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownToken);
        timeoutCts.CancelAfter(_responseTimeout);
        var readToken = timeoutCts.Token;

        return _useSocketReader
            ? await ReadSocketAsync(poolBulk, responseMode, readToken).ConfigureAwait(false)
            : await ReadStreamAsync(poolBulk, responseMode, readToken).ConfigureAwait(false);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<ResponseReadResult> ReadSocketAsync(
        bool poolBulk,
        RedisResponseMode responseMode,
        CancellationToken readToken)
    {
        var reader = _socketReaderAccessor() ?? throw new InvalidOperationException("RESP socket reader missing after connection established.");
        try
        {
            while (true)
            {
                var response = responseMode == RedisResponseMode.Default
                    ? await reader.ReadAsync(poolBulk, readToken).ConfigureAwait(false)
                    : await reader.ReadCountAsync(responseMode, readToken).ConfigureAwait(false);
                if (response.Kind == RedisRespReader.RespKind.Push)
                {
                    RedisRespReader.ReturnBuffers(response);
                    continue;
                }

                if (response.Kind == RedisRespReader.RespKind.Error)
                    return ResponseReadResult.FromResponse(response, new InvalidOperationException(response.Text ?? "Redis error"));

                return ResponseReadResult.FromResponse(response);
            }
        }
        catch (OperationCanceledException) when (_shutdownToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException oce)
        {
            return ResponseReadResult.FromError(new TimeoutException($"Redis response timed out after {_responseTimeout}.", oce));
        }
        catch (SocketException se) when (_isFatalSocket(se.SocketErrorCode))
        {
            await _failTransportAsync(se).ConfigureAwait(false);
            return ResponseReadResult.FromError(se);
        }
        catch (Exception ex)
        {
            return ResponseReadResult.FromError(ex);
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<ResponseReadResult> ReadStreamAsync(
        bool poolBulk,
        RedisResponseMode responseMode,
        CancellationToken readToken,
        RedisRespReaderState? streamReader = null)
    {
        streamReader ??= _streamReaderAccessor() ?? throw new InvalidOperationException("RESP reader missing after connection established.");
        try
        {
            while (true)
            {
                var response = responseMode == RedisResponseMode.Default
                    ? await streamReader.ReadAsync(poolBulk, readToken).ConfigureAwait(false)
                    : await streamReader.ReadCountAsync(responseMode, readToken).ConfigureAwait(false);
                if (response.Kind == RedisRespReader.RespKind.Push)
                {
                    RedisRespReader.ReturnBuffers(response);
                    continue;
                }

                if (response.Kind == RedisRespReader.RespKind.Error)
                    return ResponseReadResult.FromResponse(response, new InvalidOperationException(response.Text ?? "Redis error"));
                return ResponseReadResult.FromResponse(response);
            }
        }
        catch (OperationCanceledException) when (_shutdownToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException oce)
        {
            return ResponseReadResult.FromError(new TimeoutException($"Redis response timed out after {_responseTimeout}.", oce));
        }
        catch (IOException ioe) when (ioe.InnerException is SocketException se && _isFatalSocket(se.SocketErrorCode))
        {
            await _failTransportAsync(ioe).ConfigureAwait(false);
            return ResponseReadResult.FromError(ioe);
        }
        catch (Exception ex)
        {
            return ResponseReadResult.FromError(ex);
        }
    }
}

internal readonly struct ResponseReadResult
{
    private ResponseReadResult(RedisRespReader.RespValue response, Exception? error, bool hasResponse)
    {
        Response = response;
        Error = error;
        HasResponse = hasResponse;
    }

    public RedisRespReader.RespValue Response { get; }
    public Exception? Error { get; }
    public bool HasResponse { get; }

    public static ResponseReadResult FromResponse(RedisRespReader.RespValue response, Exception? error = null)
        => new(response, error, hasResponse: true);

    public static ResponseReadResult FromError(Exception error)
        => new(default, error, hasResponse: false);
}
