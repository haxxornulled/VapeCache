using Microsoft.Extensions.Logging.Abstractions;
using VapeCache.Abstractions.Connections;
using VapeCache.Infrastructure.Connections;
using Xunit;
using Xunit.Sdk;

namespace VapeCache.Tests.Integration;

[Collection(RedisIntegrationCollection.Name)]
public sealed class RedisPubSubIntegrationTests
{
    [SkippableFact]
    public async Task PubSub_publish_and_subscribe_round_trip()
    {
        var connectionOptions = RedisIntegrationConfig.TryLoad(out var skipReason);
        Skip.If(connectionOptions is null, skipReason);

        await using var factory = new RedisConnectionFactory(
            RedisIntegrationConfig.Monitor(connectionOptions),
            NullLogger<RedisConnectionFactory>.Instance,
            Array.Empty<IRedisConnectionObserver>());

        await using var pubSub = new RedisPubSubService(
            factory,
            RedisIntegrationConfig.Monitor(new RedisPubSubOptions
            {
                DeliveryQueueCapacity = 64,
                DropOldestOnBackpressure = true
            }),
            NullLogger<RedisPubSubService>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = cts.Token;

        var channel = "vapecache:test:pubsub:" + Guid.NewGuid().ToString("N");
        var expectedPayload = "hello-pubsub:" + Guid.NewGuid().ToString("N");
        var expectedBytes = System.Text.Encoding.UTF8.GetBytes(expectedPayload);
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var subscription = await pubSub.SubscribeAsync(
            channel,
            (message, _) =>
            {
                var text = System.Text.Encoding.UTF8.GetString(message.Payload);
                if (string.Equals(text, expectedPayload, StringComparison.Ordinal))
                    received.TrySetResult(text);
                return ValueTask.CompletedTask;
            },
            ct);

        long publishSubscribers = 0;
        for (var attempt = 0; attempt < 25; attempt++)
        {
            try
            {
                publishSubscribers = await pubSub.PublishAsync(channel, expectedBytes, ct);
            }
            catch (InvalidOperationException ex) when (
                ex.Message.Contains("NOPERM", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("NOAUTH", StringComparison.OrdinalIgnoreCase))
            {
                Skip.If(true, $"Redis user lacks PUB/SUB permissions required for integration test ({ex.Message}).");
                return;
            }

            if (publishSubscribers > 0)
                break;

            await Task.Delay(50, ct);
        }

        Assert.True(publishSubscribers > 0, "Expected Redis PUBLISH to report at least one subscriber.");

        var completed = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(5), ct));
        Assert.Same(received.Task, completed);
        Assert.Equal(expectedPayload, await received.Task);
    }
}
