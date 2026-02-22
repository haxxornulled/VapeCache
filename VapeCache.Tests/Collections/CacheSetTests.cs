using System.Buffers;
using System.Buffers.Binary;
using VapeCache.Abstractions.Caching;
using VapeCache.Infrastructure.Collections;
using VapeCache.Infrastructure.Connections;

namespace VapeCache.Tests.Collections;

public sealed class CacheSetTests
{
    [Fact]
    public async Task Set_AddAndContains_Work()
    {
        var executor = new InMemoryCommandExecutor();
        var codec = new Int32Codec();
        var set = new CacheSet<int>("tags", executor, codec);

        // Add items
        var added1 = await set.AddAsync(1);
        Assert.Equal(1, added1); // 1 = member was added

        var added2 = await set.AddAsync(2);
        Assert.Equal(1, added2);

        var added3 = await set.AddAsync(3);
        Assert.Equal(1, added3);

        // Check membership
        Assert.True(await set.ContainsAsync(1));
        Assert.True(await set.ContainsAsync(2));
        Assert.True(await set.ContainsAsync(3));
        Assert.False(await set.ContainsAsync(999));
    }

    [Fact]
    public async Task Set_AddDuplicate_DoesNotIncrementCount()
    {
        var executor = new InMemoryCommandExecutor();
        var codec = new Int32Codec();
        var set = new CacheSet<int>("unique", executor, codec);

        // Add same item twice
        var added1 = await set.AddAsync(42);
        Assert.Equal(1, added1); // 1 = member was added

        var added2 = await set.AddAsync(42);
        Assert.Equal(0, added2); // 0 = member already existed

        var count = await set.CountAsync();
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Set_RemoveAsync_RemovesMember()
    {
        var executor = new InMemoryCommandExecutor();
        var codec = new Int32Codec();
        var set = new CacheSet<int>("removable", executor, codec);

        await set.AddAsync(10);
        await set.AddAsync(20);
        await set.AddAsync(30);

        var removed1 = await set.RemoveAsync(20);
        Assert.Equal(1, removed1); // 1 = member was removed

        var removed2 = await set.RemoveAsync(20);
        Assert.Equal(0, removed2); // 0 = member didn't exist

        Assert.True(await set.ContainsAsync(10));
        Assert.False(await set.ContainsAsync(20));
        Assert.True(await set.ContainsAsync(30));
    }

    [Fact]
    public async Task Set_MembersAsync_ReturnsAllMembers()
    {
        var executor = new InMemoryCommandExecutor();
        var codec = new Int32Codec();
        var set = new CacheSet<int>("all-members", executor, codec);

        await set.AddAsync(5);
        await set.AddAsync(10);
        await set.AddAsync(15);

        var members = await set.MembersAsync();
        Assert.Equal(3, members.Length);
        Assert.Contains(5, members);
        Assert.Contains(10, members);
        Assert.Contains(15, members);
    }

    [Fact]
    public async Task Set_CountAsync_ReturnsCardinality()
    {
        var executor = new InMemoryCommandExecutor();
        var codec = new Int32Codec();
        var set = new CacheSet<int>("cardinality", executor, codec);

        var count0 = await set.CountAsync();
        Assert.Equal(0, count0);

        await set.AddAsync(1);
        await set.AddAsync(2);
        await set.AddAsync(3);

        var count3 = await set.CountAsync();
        Assert.Equal(3, count3);

        await set.RemoveAsync(2);

        var count2 = await set.CountAsync();
        Assert.Equal(2, count2);
    }

    [Fact]
    public async Task Set_StreamAsync_EmitsAllMembers()
    {
        var executor = new InMemoryCommandExecutor();
        var codec = new Int32Codec();
        var set = new CacheSet<int>("stream-test", executor, codec);

        await set.AddAsync(100);
        await set.AddAsync(200);
        await set.AddAsync(300);

        var seen = new HashSet<int>();
        await foreach (var member in set.StreamAsync())
            seen.Add(member);

        Assert.Equal(3, seen.Count);
        Assert.Contains(100, seen);
        Assert.Contains(200, seen);
        Assert.Contains(300, seen);
    }

    [Fact]
    public async Task Set_StreamAsync_WithPattern_FiltersMembers()
    {
        var executor = new InMemoryCommandExecutor();
        var codec = new StringCodec();
        var set = new CacheSet<string>("filtered", executor, codec);

        await set.AddAsync("user:123");
        await set.AddAsync("user:456");
        await set.AddAsync("admin:789");

        var users = new List<string>();
        await foreach (var member in set.StreamAsync(pattern: "user:*"))
            users.Add(member);

        Assert.Equal(2, users.Count);
        Assert.Contains("user:123", users);
        Assert.Contains("user:456", users);
    }

    [Fact]
    public async Task Set_StreamAsync_HandlesEmptySet()
    {
        var executor = new InMemoryCommandExecutor();
        var codec = new Int32Codec();
        var set = new CacheSet<int>("empty", executor, codec);

        var count = 0;
        await foreach (var _ in set.StreamAsync())
            count++;

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Set_MultipleOperations_MaintainsUniqueness()
    {
        var executor = new InMemoryCommandExecutor();
        var codec = new StringCodec();
        var set = new CacheSet<string>("unique-ops", executor, codec);

        // Add, remove, re-add
        await set.AddAsync("apple");
        await set.AddAsync("banana");
        await set.AddAsync("cherry");

        await set.RemoveAsync("banana");

        await set.AddAsync("apple"); // Duplicate
        await set.AddAsync("date");

        var members = await set.MembersAsync();
        Assert.Equal(3, members.Length); // apple, cherry, date
        Assert.Contains("apple", members);
        Assert.Contains("cherry", members);
        Assert.Contains("date", members);
        Assert.DoesNotContain("banana", members);
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
