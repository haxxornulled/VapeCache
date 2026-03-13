using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Connections;

namespace VapeCache.Infrastructure.Connections;

internal sealed partial class RedisPubSubService : IRedisPubSubService
{
    private readonly IRedisConnectionFactory _factory;
    private readonly IOptionsMonitor<RedisPubSubOptions> _optionsMonitor;
    private readonly ILogger<RedisPubSubService> _logger;
    private readonly object _subscriptionsGate = new();
    private readonly Dictionary<string, List<SubscriptionState>> _subscriptions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _publisherGate = new(1, 1);
    private readonly SemaphoreSlim _subscriberConnectionGate = new(1, 1);
    private readonly SemaphoreSlim _subscriberSendGate = new(1, 1);
    private readonly SemaphoreSlim _subscriberWakeup = new(0, int.MaxValue);
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly Task _subscriberLoopTask;

    private IRedisConnection? _publisherConnection;
    private RedisRespReaderState? _publisherReader;
    private IRedisConnection? _subscriberConnection;
    private RedisRespReaderState? _subscriberReader;
    private volatile bool _subscriberNeedsResubscribe = true;
    private int _disposed;

    public RedisPubSubService(
        IRedisConnectionFactory factory,
        IOptionsMonitor<RedisPubSubOptions> optionsMonitor,
        ILogger<RedisPubSubService> logger)
    {
        _factory = factory;
        _optionsMonitor = optionsMonitor;
        _logger = logger;
        _subscriberLoopTask = Task.Run(SubscriberLoopAsync);
    }

    public async ValueTask<long> PublishAsync(string channel, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        var options = _optionsMonitor.CurrentValue;
        EnsureEnabled(options);

        var normalizedChannel = NormalizeChannel(channel);
        var command = BuildPublishCommand(normalizedChannel, payload.Span);

        await _publisherGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await EnsurePublisherConnectionAsync(ct).ConfigureAwait(false);
                    var connection = _publisherConnection ?? throw new InvalidOperationException("Publisher connection is unavailable.");
                    var reader = _publisherReader ?? throw new InvalidOperationException("Publisher reader is unavailable.");
                    await SendCommandAsync(connection, command, ct).ConfigureAwait(false);

                    var response = await reader.ReadAsync(poolBulk: false, ct).ConfigureAwait(false);
                    try
                    {
                        if (response.Kind == RedisRespReader.RespKind.Error)
                            throw new InvalidOperationException(response.Text ?? "Redis returned an error.");
                        if (response.Kind != RedisRespReader.RespKind.Integer)
                            throw new InvalidOperationException($"Unexpected PUBLISH response kind: {response.Kind}.");

                        RedisTelemetry.CommandCalls.Add(
                            1,
                            new TagList
                            {
                                { "cmd", "PUBLISH" },
                                { "class", "pubsub" }
                            });
                        return response.IntegerValue;
                    }
                    finally
                    {
                        RedisRespReader.ReturnBuffers(response);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    await ResetPublisherConnectionUnsafeAsync().ConfigureAwait(false);
                    LogPublishAttemptFailed(_logger, ex, normalizedChannel, attempt);
                    if (attempt >= 2)
                        throw;
                }
            }
        }
        finally
        {
            _publisherGate.Release();
        }

        throw new InvalidOperationException("Publish failed unexpectedly.");
    }

    public async ValueTask<IRedisPubSubSubscription> SubscribeAsync(
        string channel,
        Func<RedisPubSubMessage, CancellationToken, ValueTask> handler,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ThrowIfDisposed();
        var options = _optionsMonitor.CurrentValue;
        EnsureEnabled(options);

        var normalizedChannel = NormalizeChannel(channel);
        var state = new SubscriptionState(
            normalizedChannel,
            handler,
            options.DeliveryQueueCapacity,
            options.DropOldestOnBackpressure,
            _logger,
            _disposeCts.Token);

        bool sendSubscribe;
        lock (_subscriptionsGate)
        {
            if (!_subscriptions.TryGetValue(normalizedChannel, out var list))
            {
                list = [];
                _subscriptions[normalizedChannel] = list;
            }

            sendSubscribe = list.Count == 0;
            list.Add(state);
        }

        _subscriberWakeup.Release();

        if (sendSubscribe && !_subscriberNeedsResubscribe)
        {
            try
            {
                await SendSubscribeAsync(normalizedChannel, ct).ConfigureAwait(false);
            }
            catch
            {
                await RemoveSubscriptionInternalAsync(state, sendRemoteUnsubscribe: false, ct).ConfigureAwait(false);
                throw;
            }
        }

        return new RedisPubSubSubscription(this, state);
    }

    private async Task SubscriberLoopAsync()
    {
        var token = _disposeCts.Token;
        var reconnectAttempt = 0;
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (!HasSubscriptions())
                {
                    await _subscriberWakeup.WaitAsync(token).ConfigureAwait(false);
                    reconnectAttempt = 0;
                    continue;
                }

                await EnsureSubscriberConnectedAndResubscribedAsync(token).ConfigureAwait(false);
                var reader = Volatile.Read(ref _subscriberReader);
                if (reader is null)
                    continue;

                var frame = await reader.ReadAsync(poolBulk: true, token).ConfigureAwait(false);
                try
                {
                    ProcessSubscriberFrame(frame);
                }
                finally
                {
                    RedisRespReader.ReturnBuffers(frame);
                }

                reconnectAttempt = 0;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                await ResetSubscriberConnectionAsync().ConfigureAwait(false);
                reconnectAttempt++;
                var delay = ComputeReconnectDelay(_optionsMonitor.CurrentValue, reconnectAttempt);
                LogSubscriberLoopFailure(_logger, ex, reconnectAttempt, delay.TotalMilliseconds);
                try
                {
                    await Task.Delay(delay, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    private void ProcessSubscriberFrame(in RedisRespReader.RespValue response)
    {
        if (!TryGetAggregateItems(response, out var items, out var count))
            return;
        if (count == 0 || items is null)
            return;

        var token = TryGetString(items[0]);
        if (string.IsNullOrEmpty(token))
            return;

        if (token.Equals("message", StringComparison.OrdinalIgnoreCase))
        {
            if (count < 3)
                return;

            var channel = TryGetString(items[1]);
            if (string.IsNullOrEmpty(channel))
                return;

            if (!TryGetPayload(items[2], out var payload))
                return;

            DispatchMessage(channel, payload);
            return;
        }
    }

    private void DispatchMessage(string channel, byte[] payload)
    {
        SubscriptionState[] subscribers;
        lock (_subscriptionsGate)
        {
            if (!_subscriptions.TryGetValue(channel, out var list) || list.Count == 0)
                return;
            subscribers = [.. list];
        }

        var message = new RedisPubSubMessage(channel, payload, DateTimeOffset.UtcNow);
        for (var i = 0; i < subscribers.Length; i++)
        {
            if (!subscribers[i].TryEnqueue(message))
            {
                RedisTelemetry.CommandFailures.Add(
                    1,
                    new TagList
                    {
                        { "cmd", "SUBSCRIBE" },
                        { "class", "pubsub" },
                        { "reason", "backpressure" }
                    });
            }
        }
    }

    private async ValueTask SendSubscribeAsync(string channel, CancellationToken ct)
    {
        await EnsureSubscriberConnectionAsync(ct).ConfigureAwait(false);
        var command = BuildSubscribeCommand(channel);

        await _subscriberSendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var connection = _subscriberConnection ?? throw new InvalidOperationException("Subscriber connection is unavailable.");
            await SendCommandAsync(connection, command, ct).ConfigureAwait(false);
        }
        finally
        {
            _subscriberSendGate.Release();
        }
    }

    private async ValueTask SendUnsubscribeAsync(string channel, CancellationToken ct)
    {
        if (_subscriberNeedsResubscribe)
            return;

        var command = BuildUnsubscribeCommand(channel);

        await _subscriberSendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var connection = Volatile.Read(ref _subscriberConnection);
            if (connection is null)
                return;
            await SendCommandAsync(connection, command, ct).ConfigureAwait(false);
        }
        finally
        {
            _subscriberSendGate.Release();
        }
    }

    private async ValueTask EnsurePublisherConnectionAsync(CancellationToken ct)
    {
        if (_publisherConnection is not null && _publisherReader is not null)
            return;

        var created = await _factory.CreateOrThrowAsync(ct).ConfigureAwait(false);
        _publisherConnection = created;
        _publisherReader = new RedisRespReaderState(created.Stream);
    }

    private async ValueTask EnsureSubscriberConnectionAsync(CancellationToken ct)
    {
        if (_subscriberConnection is not null && _subscriberReader is not null)
            return;

        await _subscriberConnectionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_subscriberConnection is not null && _subscriberReader is not null)
                return;

            var created = await _factory.CreateOrThrowAsync(ct).ConfigureAwait(false);
            _subscriberConnection = created;
            _subscriberReader = new RedisRespReaderState(created.Stream);
            _subscriberNeedsResubscribe = true;
        }
        finally
        {
            _subscriberConnectionGate.Release();
        }
    }

    private async ValueTask EnsureSubscriberConnectedAndResubscribedAsync(CancellationToken ct)
    {
        if (_subscriberConnection is not null && _subscriberReader is not null && !_subscriberNeedsResubscribe)
            return;

        await _subscriberConnectionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_subscriberConnection is null || _subscriberReader is null)
            {
                var created = await _factory.CreateOrThrowAsync(ct).ConfigureAwait(false);
                _subscriberConnection = created;
                _subscriberReader = new RedisRespReaderState(created.Stream);
                _subscriberNeedsResubscribe = true;
            }

            if (!_subscriberNeedsResubscribe)
                return;

            string[] channels;
            lock (_subscriptionsGate)
            {
                channels = [.. _subscriptions.Keys];
            }

            if (channels.Length == 0)
            {
                _subscriberNeedsResubscribe = false;
                return;
            }

            await _subscriberSendGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var connection = _subscriberConnection ?? throw new InvalidOperationException("Subscriber connection is unavailable.");
                for (var i = 0; i < channels.Length; i++)
                {
                    var command = BuildSubscribeCommand(channels[i]);
                    await SendCommandAsync(connection, command, ct).ConfigureAwait(false);
                }
            }
            finally
            {
                _subscriberSendGate.Release();
            }

            _subscriberNeedsResubscribe = false;
        }
        finally
        {
            _subscriberConnectionGate.Release();
        }
    }

    private async ValueTask RemoveSubscriptionInternalAsync(
        SubscriptionState state,
        bool sendRemoteUnsubscribe,
        CancellationToken ct)
    {
        bool becameIdle;
        bool removedLastForChannel;
        lock (_subscriptionsGate)
        {
            removedLastForChannel = false;
            if (_subscriptions.TryGetValue(state.Channel, out var list) && list.Remove(state))
            {
                if (list.Count == 0)
                {
                    _subscriptions.Remove(state.Channel);
                    removedLastForChannel = true;
                }
            }

            becameIdle = _subscriptions.Count == 0;
        }

        await state.DisposeAsync().ConfigureAwait(false);
        if (sendRemoteUnsubscribe && removedLastForChannel)
        {
            try
            {
                await SendUnsubscribeAsync(state.Channel, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogUnsubscribeFailed(_logger, ex, state.Channel);
            }
        }

        if (becameIdle)
            await ResetSubscriberConnectionAsync().ConfigureAwait(false);
    }

    private async ValueTask ResetPublisherConnectionUnsafeAsync()
    {
        var reader = Interlocked.Exchange(ref _publisherReader, null);
        if (reader is not null)
            await reader.DisposeAsync().ConfigureAwait(false);

        var connection = Interlocked.Exchange(ref _publisherConnection, null);
        if (connection is not null)
            await connection.DisposeAsync().ConfigureAwait(false);
    }

    private async ValueTask ResetSubscriberConnectionAsync()
    {
        await _subscriberConnectionGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var reader = Interlocked.Exchange(ref _subscriberReader, null);
            if (reader is not null)
                await reader.DisposeAsync().ConfigureAwait(false);

            var connection = Interlocked.Exchange(ref _subscriberConnection, null);
            if (connection is not null)
                await connection.DisposeAsync().ConfigureAwait(false);

            _subscriberNeedsResubscribe = true;
        }
        finally
        {
            _subscriberConnectionGate.Release();
        }
    }

    private static async ValueTask SendCommandAsync(IRedisConnection connection, ReadOnlyMemory<byte> command, CancellationToken ct)
    {
        var sendResult = await connection.SendAsync(command, ct).ConfigureAwait(false);
        if (!sendResult.IsSuccess)
            _ = sendResult.IfFail(static ex => throw ex);

        RedisTelemetry.BytesSent.Add(command.Length);
    }

    private static ReadOnlyMemory<byte> BuildPublishCommand(string channel, ReadOnlySpan<byte> payload)
    {
        var len = RedisRespProtocol.GetPublishCommandLength(channel, payload.Length);
        var command = GC.AllocateUninitializedArray<byte>(len);
        _ = RedisRespProtocol.WritePublishCommand(command, channel, payload);
        return command;
    }

    private static ReadOnlyMemory<byte> BuildSubscribeCommand(string channel)
    {
        var len = RedisRespProtocol.GetSubscribeCommandLength(channel);
        var command = GC.AllocateUninitializedArray<byte>(len);
        _ = RedisRespProtocol.WriteSubscribeCommand(command, channel);
        return command;
    }

    private static ReadOnlyMemory<byte> BuildUnsubscribeCommand(string channel)
    {
        var len = RedisRespProtocol.GetUnsubscribeCommandLength(channel);
        var command = GC.AllocateUninitializedArray<byte>(len);
        _ = RedisRespProtocol.WriteUnsubscribeCommand(command, channel);
        return command;
    }

    private static string NormalizeChannel(string channel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        return channel.Trim();
    }

    private static void EnsureEnabled(in RedisPubSubOptions options)
    {
        if (!options.Enabled)
            throw new InvalidOperationException("Redis pub/sub is disabled by configuration.");
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
    }

    private bool HasSubscriptions()
    {
        lock (_subscriptionsGate)
        {
            return _subscriptions.Count > 0;
        }
    }

    private static TimeSpan ComputeReconnectDelay(RedisPubSubOptions options, int attempt)
    {
        var min = options.ReconnectDelayMin;
        var max = options.ReconnectDelayMax;
        if (attempt <= 1)
            return min;

        var factor = Math.Pow(2d, Math.Min(8, attempt - 1));
        var scaledTicks = min.Ticks * factor;
        if (scaledTicks >= max.Ticks)
            return max;

        return TimeSpan.FromTicks((long)scaledTicks);
    }

    private static bool TryGetAggregateItems(in RedisRespReader.RespValue value, out RedisRespReader.RespValue[]? items, out int count)
    {
        if (value.Kind == RedisRespReader.RespKind.Array || value.Kind == RedisRespReader.RespKind.Push)
        {
            items = value.ArrayItems;
            count = value.ArrayLength;
            return true;
        }

        items = null;
        count = 0;
        return false;
    }

    private static string? TryGetString(in RedisRespReader.RespValue value)
    {
        return value.Kind switch
        {
            RedisRespReader.RespKind.SimpleString => value.Text,
            RedisRespReader.RespKind.Error => value.Text,
            RedisRespReader.RespKind.BulkString when value.Bulk is not null => Encoding.UTF8.GetString(value.Bulk, 0, value.BulkLength),
            RedisRespReader.RespKind.Integer => value.IntegerValue.ToString(CultureInfo.InvariantCulture),
            RedisRespReader.RespKind.NullBulkString => null,
            _ => null
        };
    }

    private static bool TryGetPayload(in RedisRespReader.RespValue value, out byte[] payload)
    {
        switch (value.Kind)
        {
            case RedisRespReader.RespKind.BulkString:
                if (value.Bulk is null)
                {
                    payload = Array.Empty<byte>();
                    return true;
                }

                payload = GC.AllocateUninitializedArray<byte>(value.BulkLength);
                value.Bulk.AsSpan(0, value.BulkLength).CopyTo(payload);
                return true;

            case RedisRespReader.RespKind.NullBulkString:
                payload = Array.Empty<byte>();
                return true;

            case RedisRespReader.RespKind.SimpleString:
                payload = value.Text is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(value.Text);
                return true;

            default:
                payload = Array.Empty<byte>();
                return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        _disposeCts.Cancel();
        _subscriberWakeup.Release();

        SubscriptionState[] states;
        lock (_subscriptionsGate)
        {
            states = [.. _subscriptions.Values.SelectMany(static list => list)];
            _subscriptions.Clear();
        }

        for (var i = 0; i < states.Length; i++)
            await states[i].DisposeAsync().ConfigureAwait(false);

        try
        {
            await _subscriberLoopTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }

        await _publisherGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await ResetPublisherConnectionUnsafeAsync().ConfigureAwait(false);
        }
        finally
        {
            _publisherGate.Release();
        }

        await ResetSubscriberConnectionAsync().ConfigureAwait(false);

        _disposeCts.Dispose();
        _publisherGate.Dispose();
        _subscriberConnectionGate.Dispose();
        _subscriberSendGate.Dispose();
        _subscriberWakeup.Dispose();
    }

    private sealed class RedisPubSubSubscription : IRedisPubSubSubscription
    {
        private readonly RedisPubSubService _owner;
        private readonly SubscriptionState _state;
        private int _disposed;

        public RedisPubSubSubscription(RedisPubSubService owner, SubscriptionState state)
        {
            _owner = owner;
            _state = state;
        }

        public string Channel => _state.Channel;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
                return;

            await _owner.RemoveSubscriptionInternalAsync(
                _state,
                sendRemoteUnsubscribe: true,
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    private sealed class SubscriptionState : IAsyncDisposable
    {
        private readonly Func<RedisPubSubMessage, CancellationToken, ValueTask> _handler;
        private readonly ILogger _logger;
        private readonly Channel<RedisPubSubMessage> _channel;
        private readonly CancellationTokenSource _cts;
        private readonly Task _processorTask;
        private readonly bool _dropOldest;
        private int _disposed;

        public SubscriptionState(
            string channel,
            Func<RedisPubSubMessage, CancellationToken, ValueTask> handler,
            int queueCapacity,
            bool dropOldestOnBackpressure,
            ILogger logger,
            CancellationToken ownerToken)
        {
            Channel = channel;
            _handler = handler;
            _logger = logger;
            _dropOldest = dropOldestOnBackpressure;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ownerToken);
            _channel = System.Threading.Channels.Channel.CreateBounded<RedisPubSubMessage>(new BoundedChannelOptions(queueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
            _processorTask = Task.Run(ProcessLoopAsync, CancellationToken.None);
        }

        public string Channel { get; }

        public bool TryEnqueue(RedisPubSubMessage message)
        {
            if (_channel.Writer.TryWrite(message))
                return true;

            if (_dropOldest)
            {
                while (_channel.Reader.TryRead(out _))
                {
                    RedisTelemetry.CommandFailures.Add(
                        1,
                        new TagList
                        {
                            { "cmd", "SUBSCRIBE" },
                            { "class", "pubsub" },
                            { "reason", "backpressure" }
                        });
                    if (_channel.Writer.TryWrite(message))
                        return true;
                }
            }

            return false;
        }

        private async Task ProcessLoopAsync()
        {
            try
            {
                while (await _channel.Reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
                {
                    while (_channel.Reader.TryRead(out var message))
                    {
                        try
                        {
                            await _handler(message, _cts.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                        {
                            return;
                        }
                        catch (Exception ex)
                        {
                            LogMessageHandlerFailed(_logger, ex, Channel);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                // shutdown
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
                return;

            _channel.Writer.TryComplete();
            _cts.Cancel();
            try
            {
                await _processorTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                // shutdown
            }
            finally
            {
                _cts.Dispose();
            }
        }
    }

    [LoggerMessage(
        EventId = 5601,
        Level = LogLevel.Warning,
        Message = "Redis pub/sub publish attempt failed. Channel={Channel} Attempt={Attempt}")]
    private static partial void LogPublishAttemptFailed(
        ILogger logger,
        Exception exception,
        string channel,
        int attempt);

    [LoggerMessage(
        EventId = 5602,
        Level = LogLevel.Warning,
        Message = "Redis pub/sub subscriber loop failure. Attempt={Attempt} ReconnectDelayMs={DelayMs}")]
    private static partial void LogSubscriberLoopFailure(
        ILogger logger,
        Exception exception,
        int attempt,
        double delayMs);

    [LoggerMessage(
        EventId = 5603,
        Level = LogLevel.Debug,
        Message = "Redis pub/sub unsubscribe failed for channel {Channel}.")]
    private static partial void LogUnsubscribeFailed(
        ILogger logger,
        Exception exception,
        string channel);

    [LoggerMessage(
        EventId = 5604,
        Level = LogLevel.Warning,
        Message = "Redis pub/sub handler failed for channel {Channel}.")]
    private static partial void LogMessageHandlerFailed(
        ILogger logger,
        Exception exception,
        string channel);
}
