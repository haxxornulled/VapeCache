using System.Buffers;
using System.Buffers.Binary;
using VapeCache.Abstractions.Caching;
using VapeCache.Infrastructure.Collections;
using VapeCache.Infrastructure.Connections;

namespace VapeCache.Tests.Collections;

public sealed class CacheListTests
{
    [Fact]
    public async Task List_PushFrontAndPopFront_Work()
    {
        var executor = new InMemoryCommandExecutor();
        var codec = new Int32Codec();
        var list = new CacheList<int>("queue", executor, codec);

        // Push items to front
        var len1 = await list.PushFrontAsync(1);
        Assert.Equal(1, len1);

        var len2 = await list.PushFrontAsync(2);
        Assert.Equal(2, len2);

        var len3 = await list.PushFrontAsync(3);
        Assert.Equal(3, len3);

        // Pop from front (LIFO order)
        var item1 = await list.PopFrontAsync();
        Assert.Equal(3, item1);

        var item2 = await list.PopFrontAsync();
        Assert.Equal(2, item2);

        var item3 = await list.PopFrontAsync();
        Assert.Equal(1, item3);

        // Empty list returns default value (0 for int)
        var empty = await list.PopFrontAsync();
        Assert.Equal(0, empty);
    }

    [Fact]
    public async Task List_PushBackAndPopBack_Work()
    {
        var executor = new InMemoryCommandExecutor();
        var codec = new Int32Codec();
        var list = new CacheList<int>("stack", executor, codec);

        // Push items to back
        await list.PushBackAsync(10);
        await list.PushBackAsync(20);
        await list.PushBackAsync(30);

        // Pop from back (LIFO order)
        var item1 = await list.PopBackAsync();
        Assert.Equal(30, item1);

        var item2 = await list.PopBackAsync();
        Assert.Equal(20, item2);

        var item3 = await list.PopBackAsync();
        Assert.Equal(10, item3);
    }

    [Fact]
    public async Task List_TryPopFrontAsync_ReturnsCorrectly()
    {
        var executor = new InMemoryCommandExecutor();
        var codec = new Int32Codec();
        var list = new CacheList<int>("try-queue", executor, codec);

        await list.PushFrontAsync(100);

        // Should succeed when item exists
        var success = list.TryPopFrontAsync(CancellationToken.None, out var task);
        Assert.True(success);

        var value = await task;
        Assert.Equal(100, value);

        // Empty list behavior depends on executor implementation
        var success2 = list.TryPopFrontAsync(CancellationToken.None, out var task2);
        if (success2)
        {
            var value2 = await task2;
            Assert.Equal(0, value2); // Empty list returns default value (0 for int)
        }
    }

    [Fact]
    public async Task List_TryPopBackAsync_ReturnsCorrectly()
    {
        var executor = new InMemoryCommandExecutor();
        var codec = new Int32Codec();
        var list = new CacheList<int>("try-stack", executor, codec);

        await list.PushBackAsync(200);

        var success = list.TryPopBackAsync(CancellationToken.None, out var task);
        Assert.True(success);

        var value = await task;
        Assert.Equal(200, value);
    }

    [Fact]
    public async Task List_RangeAsync_ReturnsSlice()
    {
        var executor = new InMemoryCommandExecutor();
        var codec = new Int32Codec();
        var list = new CacheList<int>("items", executor, codec);

        // Push items
        await list.PushBackAsync(1);
        await list.PushBackAsync(2);
        await list.PushBackAsync(3);
        await list.PushBackAsync(4);
        await list.PushBackAsync(5);

        // Get all items
        var all = await list.RangeAsync(0, -1);
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, all);

        // Get first 3 items
        var first3 = await list.RangeAsync(0, 2);
        Assert.Equal(new[] { 1, 2, 3 }, first3);

        // Get last 2 items
        var last2 = await list.RangeAsync(-2, -1);
        Assert.Equal(new[] { 4, 5 }, last2);
    }

    [Fact]
    public async Task List_LengthAsync_ReturnsCount()
    {
        var executor = new InMemoryCommandExecutor();
        var codec = new Int32Codec();
        var list = new CacheList<int>("length-test", executor, codec);

        var len0 = await list.LengthAsync();
        Assert.Equal(0, len0);

        await list.PushBackAsync(1);
        await list.PushBackAsync(2);
        await list.PushBackAsync(3);

        var len3 = await list.LengthAsync();
        Assert.Equal(3, len3);

        await list.PopFrontAsync();

        var len2 = await list.LengthAsync();
        Assert.Equal(2, len2);
    }

    [Fact]
    public async Task List_StreamAsync_EmitsAllItems()
    {
        var executor = new InMemoryCommandExecutor();
        var codec = new Int32Codec();
        var list = new CacheList<int>("stream-test", executor, codec);

        await list.PushBackAsync(10);
        await list.PushBackAsync(20);
        await list.PushBackAsync(30);

        var items = new List<int>();
        await foreach (var item in list.StreamAsync())
            items.Add(item);

        Assert.Equal(new[] { 10, 20, 30 }, items);
    }

    [Fact]
    public async Task List_StreamAsync_WithPageSize_Works()
    {
        var executor = new InMemoryCommandExecutor();
        var codec = new Int32Codec();
        var list = new CacheList<int>("paged-stream", executor, codec);

        // Add 10 items
        for (int i = 1; i <= 10; i++)
            await list.PushBackAsync(i);

        var items = new List<int>();
        await foreach (var item in list.StreamAsync(pageSize: 3))
            items.Add(item);

        Assert.Equal(10, items.Count);
        Assert.Equal(Enumerable.Range(1, 10), items);
    }

    [Fact]
    public async Task List_StreamAsync_InvalidPageSize_Throws()
    {
        var executor = new InMemoryCommandExecutor();
        var codec = new Int32Codec();
        var list = new CacheList<int>("invalid-page", executor, codec);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
        {
            await foreach (var _ in list.StreamAsync(pageSize: 0))
            {
            }
        });

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
        {
            await foreach (var _ in list.StreamAsync(pageSize: -1))
            {
            }
        });
    }

    [Fact]
    public async Task List_StreamAsync_HandlesEmptyList()
    {
        var executor = new InMemoryCommandExecutor();
        var codec = new Int32Codec();
        var list = new CacheList<int>("empty-stream", executor, codec);

        var count = 0;
        await foreach (var _ in list.StreamAsync())
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
}
