using System.Text;
using VapeCache.Infrastructure.Connections;
using Xunit;

namespace VapeCache.Tests.Connections;

public class RedisRespProtocolTests
{
    [Fact]
    public void FtSearch_WithLimit_UsesSixParts()
    {
        var len = RedisRespProtocol.GetFtSearchCommandLength("idx", "*", 0, 1);
        var buffer = new byte[len];
        _ = RedisRespProtocol.WriteFtSearchCommand(buffer, "idx", "*", 0, 1);

        var text = Encoding.ASCII.GetString(buffer);
        Assert.StartsWith("*6\r\n", text);
    }

    [Fact]
    public void FtSearch_WithoutLimit_UsesThreeParts()
    {
        var len = RedisRespProtocol.GetFtSearchCommandLength("idx", "*", null, null);
        var buffer = new byte[len];
        _ = RedisRespProtocol.WriteFtSearchCommand(buffer, "idx", "*", null, null);

        var text = Encoding.ASCII.GetString(buffer);
        Assert.StartsWith("*3\r\n", text);
    }

    [Fact]
    public void ZRangeByScoreWithLimit_UsesEightParts()
    {
        var len = RedisRespProtocol.GetZRangeByScoreWithScoresCommandLength("scores", "0", "10", descending: false, offset: 1, count: 2);
        var buffer = new byte[len];
        _ = RedisRespProtocol.WriteZRangeByScoreWithScoresCommand(buffer, "scores", "0", "10", descending: false, offset: 1, count: 2);

        var text = Encoding.ASCII.GetString(buffer);
        Assert.StartsWith("*8\r\n", text);
        Assert.Contains("$5\r\nLIMIT\r\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RPushMany_LengthMatchesWriter()
    {
        var values = new ReadOnlyMemory<byte>[]
        {
            Encoding.UTF8.GetBytes("{\"product\":\"p1\",\"qty\":2}"),
            Encoding.UTF8.GetBytes("x"),
            Encoding.UTF8.GetBytes("payload-12345")
        };

        var len = RedisRespProtocol.GetRPushManyCommandLength("cart:user-000001", values, values.Length);
        var buffer = new byte[len];
        var written = RedisRespProtocol.WriteRPushManyCommand(buffer, "cart:user-000001", values, values.Length);

        Assert.Equal(len, written);

        var text = Encoding.UTF8.GetString(buffer);
        Assert.StartsWith($"*{2 + values.Length}\r\n", text);
        Assert.Contains("RPUSH", text, StringComparison.Ordinal);
        Assert.Contains("cart:user-000001", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SkipHelloResponseAsync_ConsumesResp3MapPayload()
    {
        var payload = Encoding.UTF8.GetBytes(
            "%7\r\n" +
            "+server\r\n$5\r\nredis\r\n" +
            "+version\r\n$5\r\n7.2.0\r\n" +
            "+proto\r\n:2\r\n" +
            "+id\r\n:1\r\n" +
            "+mode\r\n$10\r\nstandalone\r\n" +
            "+role\r\n$6\r\nmaster\r\n" +
            "+modules\r\n*0\r\n");

        await using var stream = new MemoryStream(payload);
        await RedisRespProtocol.SkipHelloResponseAsync(stream, CancellationToken.None);

        Assert.Equal(stream.Length, stream.Position);
    }

    [Fact]
    public async Task SkipHelloResponseAsync_ConsumesAttributeWrappedResponse()
    {
        var payload = Encoding.UTF8.GetBytes(
            "|1\r\n" +
            "+meta\r\n+warmup\r\n" +
            "+OK\r\n");

        await using var stream = new MemoryStream(payload);
        await RedisRespProtocol.SkipHelloResponseAsync(stream, CancellationToken.None);

        Assert.Equal(stream.Length, stream.Position);
    }

    [Fact]
    public async Task SkipHelloResponseAsync_ThrowsOnRedisError()
    {
        var payload = Encoding.UTF8.GetBytes("-ERR unknown command 'HELLO'\r\n");

        await using var stream = new MemoryStream(payload);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => RedisRespProtocol.SkipHelloResponseAsync(stream, CancellationToken.None));
    }
}
