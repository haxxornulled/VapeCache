using System;
using System.Buffers;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VapeCache.Infrastructure.Connections;
using Xunit;

namespace VapeCache.Tests.Connections;

public sealed class RedisRespReaderTests
{
    [Fact]
    public async Task ReadAsync_ThrowsWhenBulkStringExceedsMax()
    {
        var payload = Encoding.UTF8.GetBytes("$5\r\nhello\r\n");
        await using var stream = new MemoryStream(payload);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await RedisRespReader.ReadAsync(stream, CancellationToken.None, maxBulkStringBytes: 4, maxArrayDepth: 64));
    }

    [Fact]
    public async Task ReadAsync_ThrowsWhenArrayDepthExceedsMax()
    {
        var payload = Encoding.UTF8.GetBytes("*1\r\n*1\r\n*1\r\n+OK\r\n");
        await using var stream = new MemoryStream(payload);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await RedisRespReader.ReadAsync(stream, CancellationToken.None, maxBulkStringBytes: 1024, maxArrayDepth: 2));
    }

    [Fact]
    public async Task ReadAsync_AllowsArrayDepthAtLimit()
    {
        var payload = Encoding.UTF8.GetBytes("*1\r\n*1\r\n*1\r\n+OK\r\n");
        await using var stream = new MemoryStream(payload);

        var resp = await RedisRespReader.ReadAsync(stream, CancellationToken.None, maxBulkStringBytes: 1024, maxArrayDepth: 3);

        Assert.Equal(RedisRespReader.RespKind.Array, resp.Kind);
        Assert.Equal(1, resp.ArrayLength);
        Assert.NotNull(resp.ArrayItems);
    }

    [Fact]
    public async Task ReadAsync_ParsesResp3MapAsFlattenedArray()
    {
        var payload = Encoding.UTF8.GetBytes("%2\r\n+one\r\n:1\r\n+two\r\n:2\r\n");
        await using var stream = new MemoryStream(payload);

        var resp = await RedisRespReader.ReadAsync(stream, CancellationToken.None, maxBulkStringBytes: 1024, maxArrayDepth: 8);
        try
        {
            Assert.Equal(RedisRespReader.RespKind.Array, resp.Kind);
            Assert.Equal(4, resp.ArrayLength);
            Assert.NotNull(resp.ArrayItems);
            Assert.Equal("one", resp.ArrayItems![0].Text);
            Assert.Equal(1, resp.ArrayItems[1].IntegerValue);
            Assert.Equal("two", resp.ArrayItems[2].Text);
            Assert.Equal(2, resp.ArrayItems[3].IntegerValue);
        }
        finally
        {
            RedisRespReader.ReturnBuffers(resp);
        }
    }

    [Fact]
    public async Task ReadAsync_ParsesResp3PushFrame()
    {
        var payload = Encoding.UTF8.GetBytes(">2\r\n+invalidate\r\n$3\r\nkey\r\n");
        await using var stream = new MemoryStream(payload);

        var resp = await RedisRespReader.ReadAsync(stream, CancellationToken.None, maxBulkStringBytes: 1024, maxArrayDepth: 8);
        try
        {
            Assert.Equal(RedisRespReader.RespKind.Push, resp.Kind);
            Assert.Equal(2, resp.ArrayLength);
            Assert.NotNull(resp.ArrayItems);
            Assert.Equal("invalidate", resp.ArrayItems![0].Text);
            Assert.Equal("key", Encoding.UTF8.GetString(resp.ArrayItems[1].Bulk!, 0, resp.ArrayItems[1].BulkLength));
        }
        finally
        {
            RedisRespReader.ReturnBuffers(resp);
        }
    }

    [Fact]
    public async Task ReadAsync_ConsumesResp3AttributesAndReturnsWrappedValue()
    {
        var payload = Encoding.UTF8.GetBytes("|1\r\n+meta\r\n+warmup\r\n$5\r\nhello\r\n");
        await using var stream = new MemoryStream(payload);

        var resp = await RedisRespReader.ReadAsync(stream, CancellationToken.None, maxBulkStringBytes: 1024, maxArrayDepth: 8);

        Assert.Equal(RedisRespReader.RespKind.BulkString, resp.Kind);
        Assert.Equal("hello", Encoding.UTF8.GetString(resp.Bulk!, 0, resp.BulkLength));
    }

    [Fact]
    public async Task ReadAsync_ParsesResp3VerbatimStringPayload()
    {
        var payload = Encoding.UTF8.GetBytes("=9\r\ntxt:hello\r\n");
        await using var stream = new MemoryStream(payload);

        var resp = await RedisRespReader.ReadAsync(stream, CancellationToken.None, maxBulkStringBytes: 1024, maxArrayDepth: 8);

        Assert.Equal(RedisRespReader.RespKind.BulkString, resp.Kind);
        Assert.Equal("hello", Encoding.UTF8.GetString(resp.Bulk!, 0, resp.BulkLength));
    }

    [Fact]
    public void ReturnArray_ClearsTailReferences_WhenTlsCachedArrayIsReused()
    {
        // Drain any pre-existing TLS cache entry so this test controls the cached instance.
        var drained = RedisRespReader.RentArray(1);
        Array.Clear(drained, 0, drained.Length);
        ArrayPool<RedisRespReader.RespValue>.Shared.Return(drained, clearArray: false);

        var array = RedisRespReader.RentArray(8);
        for (var i = 0; i < array.Length; i++)
            array[i] = RedisRespReader.RespValue.SimpleString($"sentinel-{i}");

        RedisRespReader.ReturnArray(array, length: 2);

        var reused = RedisRespReader.RentArray(8);
        try
        {
            Assert.Same(array, reused);
            for (var i = 2; i < reused.Length; i++)
            {
                var cleared = reused[i];
                Assert.Null(cleared.Text);
                Assert.Null(cleared.Bulk);
                Assert.Null(cleared.ArrayItems);
                Assert.Equal(0, cleared.BulkLength);
                Assert.Equal(0, cleared.ArrayLength);
            }
        }
        finally
        {
            RedisRespReader.ReturnArray(reused, reused.Length);
        }
    }
}
