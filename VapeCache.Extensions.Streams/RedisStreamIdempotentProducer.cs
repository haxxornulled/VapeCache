using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Connections;
using VapeCache.Guards;

namespace VapeCache.Extensions.Streams;

internal sealed class RedisStreamIdempotentProducer(
    IRedisCommandExecutor redis,
    IOptionsMonitor<RedisStreamIdempotentProducerOptions> options) : IRedisStreamIdempotentProducer
{
    public ValueTask<string> PublishAsync(
        string key,
        string producerId,
        string? idempotentId,
        (string Field, ReadOnlyMemory<byte> Value)[] fields,
        CancellationToken ct = default)
    {
        ParanoiaThrowGuard.Against.NotNull(redis);
        ParanoiaThrowGuard.Against.NotNullOrWhiteSpace(key);
        ParanoiaThrowGuard.Against.NotNullOrWhiteSpace(producerId);
        ParanoiaThrowGuard.Against.NotNull(fields);

        if (fields.Length == 0)
            throw new ArgumentException("At least one stream field/value pair is required.", nameof(fields));

        var current = options.CurrentValue;
        var useAutoIdempotentId = current.UseAutoIdempotentId;
        if (!useAutoIdempotentId)
            ParanoiaThrowGuard.Against.NotNullOrWhiteSpace(idempotentId);

        return redis.XAddIdempotentAsync(
            key,
            producerId,
            idempotentId,
            useAutoIdempotentId,
            current.DefaultEntryId,
            fields,
            ct);
    }

    public ValueTask<bool> ConfigureIdempotenceAsync(
        string key,
        int? durationSeconds,
        int? maxSize,
        CancellationToken ct = default)
    {
        ParanoiaThrowGuard.Against.NotNull(redis);
        ParanoiaThrowGuard.Against.NotNullOrWhiteSpace(key);

        return redis.XCfgSetIdempotenceAsync(key, durationSeconds, maxSize, ct);
    }
}
