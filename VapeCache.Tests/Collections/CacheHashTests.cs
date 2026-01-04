using System.Buffers;
using System.Buffers.Binary;
using VapeCache.Abstractions.Caching;
using VapeCache.Infrastructure.Collections;
using VapeCache.Infrastructure.Connections;

namespace VapeCache.Tests.Collections;

public sealed class CacheHashTests
{
    [Fact]
    public async Task Hash_SetGetAndGetMany_Work()
    {
        var executor = new InMemoryCommandExecutor();
        var codec = new Int32Codec();
        var hash = new CacheHash<int>("user:123", executor, codec);

        // Test Set and Get
        var setResult = await hash.SetAsync("age", 25);
        Assert.Equal(1, setResult); // 1 = field was added

        var age = await hash.GetAsync("age");
        Assert.Equal(25, age);

        // Test missing field (returns default value for int, which is 0)
        var missing = await hash.GetAsync("nonexistent");
        Assert.Equal(0, missing);

        // Test multiple fields
        await hash.SetAsync("score", 100);
        await hash.SetAsync("level", 5);

        var values = await hash.GetManyAsync(new[] { "age", "score", "level", "missing" });
        Assert.Equal(4, values.Length);
        Assert.Equal(25, values[0]);
        Assert.Equal(100, values[1]);
        Assert.Equal(5, values[2]);
        Assert.Equal(0, values[3]); // Missing field returns default value (0 for int)
    }

    [Fact]
    public async Task Hash_Update_OverwritesExistingField()
    {
        var executor = new InMemoryCommandExecutor();
        var codec = new Int32Codec();
        var hash = new CacheHash<int>("settings", executor, codec);

        // Set initial value
        var result1 = await hash.SetAsync("timeout", 30);
        Assert.Equal(1, result1); // 1 = field was added

        // Update existing value
        var result2 = await hash.SetAsync("timeout", 60);
        Assert.Equal(0, result2); // 0 = field was updated (not added)

        var value = await hash.GetAsync("timeout");
        Assert.Equal(60, value);
    }

    [Fact]
    public async Task Hash_StreamAsync_EmitsAllFields()
    {
        var executor = new InMemoryCommandExecutor();
        var codec = new Int32Codec();
        var hash = new CacheHash<int>("metrics", executor, codec);

        await hash.SetAsync("requests", 1000);
        await hash.SetAsync("errors", 5);
        await hash.SetAsync("latency", 250);

        var seen = new Dictionary<string, int>();
        await foreach (var (field, value) in hash.StreamAsync())
            seen[field] = value;

        Assert.Equal(3, seen.Count);
        Assert.Equal(1000, seen["requests"]);
        Assert.Equal(5, seen["errors"]);
        Assert.Equal(250, seen["latency"]);
    }

    [Fact]
    public async Task Hash_StreamAsync_WithPattern_FiltersFields()
    {
        var executor = new InMemoryCommandExecutor();
        var codec = new StringCodec();
        var hash = new CacheHash<string>("config", executor, codec);

        await hash.SetAsync("redis:host", "localhost");
        await hash.SetAsync("redis:port", "6379");
        await hash.SetAsync("cache:ttl", "3600");

        var redisFields = new List<string>();
        await foreach (var (field, _) in hash.StreamAsync(pattern: "redis:*"))
            redisFields.Add(field);

        Assert.Equal(2, redisFields.Count);
        Assert.Contains("redis:host", redisFields);
        Assert.Contains("redis:port", redisFields);
    }

    [Fact]
    public async Task Hash_StreamAsync_HandlesEmptyHash()
    {
        var executor = new InMemoryCommandExecutor();
        var codec = new Int32Codec();
        var hash = new CacheHash<int>("empty", executor, codec);

        var count = 0;
        await foreach (var _ in hash.StreamAsync())
            count++;

        Assert.Equal(0, count);
    }

    private sealed class Int32Codec : ICacheCodec<int>
    {
        public void Serialize(IBufferWriter<byte> buffer, int value)
        {
            var span = buffer.GetSpan(sizeof(int));
            BinaryPrimitives.WriteInt32LittleEndian(span, value);
            buffer.Advance(sizeof(int));
        }

        public int Deserialize(ReadOnlySpan<byte> data)
            => BinaryPrimitives.ReadInt32LittleEndian(data);
    }

    private sealed class StringCodec : ICacheCodec<string>
    {
        public void Serialize(IBufferWriter<byte> buffer, string value)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(value);
            buffer.Write(bytes);
        }

        public string Deserialize(ReadOnlySpan<byte> data)
            => System.Text.Encoding.UTF8.GetString(data);
    }
}
