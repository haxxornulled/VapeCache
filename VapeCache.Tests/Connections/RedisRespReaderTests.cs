using System;
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
}
