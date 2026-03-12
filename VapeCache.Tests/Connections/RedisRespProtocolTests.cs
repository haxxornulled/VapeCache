using System.Text;
using VapeCache.Infrastructure.Connections;
using Xunit;

namespace VapeCache.Tests.Connections;

public class RedisRespProtocolTests
{
    private delegate int WriteSingleKeyCommand(Span<byte> destination, string key);

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

    [Fact]
    public void PSetEx_LengthMatchesWriter()
    {
        var value = "v1"u8.ToArray();
        var len = RedisRespProtocol.GetPSetExCommandLength("k1", value.Length, 1234);
        var buffer = new byte[len];
        var written = RedisRespProtocol.WritePSetExCommand(buffer, "k1", value, 1234);

        Assert.Equal(len, written);

        var text = Encoding.ASCII.GetString(buffer);
        Assert.StartsWith("*4\r\n", text, StringComparison.Ordinal);
        Assert.Contains("$6\r\nPSETEX\r\n", text, StringComparison.Ordinal);
        Assert.Contains("$2\r\nk1\r\n", text, StringComparison.Ordinal);
        Assert.Contains("$4\r\n1234\r\n", text, StringComparison.Ordinal);
        Assert.EndsWith("$2\r\nv1\r\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void PSetEx_HeaderMatchesFullCommandPrefix()
    {
        var value = "v1"u8.ToArray();
        var fullLen = RedisRespProtocol.GetPSetExCommandLength("k1", value.Length, 1234);
        var fullBuffer = new byte[fullLen];
        _ = RedisRespProtocol.WritePSetExCommand(fullBuffer, "k1", value, 1234);

        var headerLen = fullLen - value.Length - 2;
        var headerBuffer = new byte[headerLen];
        var written = RedisRespProtocol.WritePSetExCommandHeader(headerBuffer, "k1", 1234, value.Length);

        Assert.Equal(headerLen, written);
        Assert.True(fullBuffer.AsSpan(0, headerLen).SequenceEqual(headerBuffer));
        Assert.EndsWith("$2\r\n", Encoding.ASCII.GetString(headerBuffer), StringComparison.Ordinal);
    }

    [Fact]
    public void PrefixedSingleKeyCommands_LengthMatchesWriter()
    {
        const string key = "user:42";

        AssertSingleKeyCommand(key, "$3\r\nGET\r\n", RedisRespProtocol.GetGetCommandLength, RedisRespProtocol.WriteGetCommand);
        AssertSingleKeyCommand(key, "$3\r\nTTL\r\n", RedisRespProtocol.GetTtlCommandLength, RedisRespProtocol.WriteTtlCommand);
        AssertSingleKeyCommand(key, "$4\r\nPTTL\r\n", RedisRespProtocol.GetPTtlCommandLength, RedisRespProtocol.WritePTtlCommand);
        AssertSingleKeyCommand(key, "$3\r\nDEL\r\n", RedisRespProtocol.GetDelCommandLength, RedisRespProtocol.WriteDelCommand);
        AssertSingleKeyCommand(key, "$6\r\nUNLINK\r\n", RedisRespProtocol.GetUnlinkCommandLength, RedisRespProtocol.WriteUnlinkCommand);
    }

    [Fact]
    public void HGet_LengthMatchesWriter()
    {
        const string key = "hash:42";
        const string field = "field:alpha";

        var len = RedisRespProtocol.GetHGetCommandLength(key, field);
        var buffer = new byte[len];
        var written = RedisRespProtocol.WriteHGetCommand(buffer, key, field);

        Assert.Equal(len, written);
        var text = Encoding.ASCII.GetString(buffer);
        Assert.StartsWith("*3\r\n", text, StringComparison.Ordinal);
        Assert.Contains("$4\r\nHGET\r\n", text, StringComparison.Ordinal);
        Assert.Contains("$7\r\nhash:42\r\n", text, StringComparison.Ordinal);
        Assert.Contains("$11\r\nfield:alpha\r\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void HSet_LengthAndHeaderMatchFullCommandPrefix()
    {
        const string key = "hash:42";
        const string field = "f1";
        var value = "value-123"u8.ToArray();

        var fullLen = RedisRespProtocol.GetHSetCommandLength(key, field, value.Length);
        var fullBuffer = new byte[fullLen];
        var written = RedisRespProtocol.WriteHSetCommand(fullBuffer, key, field, value);
        Assert.Equal(fullLen, written);

        var headerLen = fullLen - value.Length - 2;
        var headerBuffer = new byte[headerLen];
        var headerWritten = RedisRespProtocol.WriteHSetCommandHeader(headerBuffer, key, field, value.Length);
        Assert.Equal(headerLen, headerWritten);
        Assert.True(fullBuffer.AsSpan(0, headerLen).SequenceEqual(headerBuffer));
        Assert.EndsWith($"${value.Length}\r\n", Encoding.ASCII.GetString(headerBuffer), StringComparison.Ordinal);
    }

    [Fact]
    public void Set_WithoutTtl_LengthAndHeaderMatch()
    {
        const string key = "cache:key:1";
        var value = "abc123"u8.ToArray();

        var fullLen = RedisRespProtocol.GetSetCommandLength(key, value.Length, ttlMs: null);
        var fullBuffer = new byte[fullLen];
        var fullWritten = RedisRespProtocol.WriteSetCommand(fullBuffer, key, value, ttlMs: null);
        Assert.Equal(fullLen, fullWritten);

        var fullText = Encoding.ASCII.GetString(fullBuffer);
        Assert.StartsWith("*3\r\n", fullText, StringComparison.Ordinal);
        Assert.Contains("$3\r\nSET\r\n", fullText, StringComparison.Ordinal);
        Assert.DoesNotContain("$2\r\nPX\r\n", fullText, StringComparison.Ordinal);

        var headerLen = fullLen - value.Length - 2;
        var headerBuffer = new byte[headerLen];
        var headerWritten = RedisRespProtocol.WriteSetCommandHeader(headerBuffer, key, value.Length, ttlMs: null);
        Assert.Equal(headerLen, headerWritten);
        Assert.True(fullBuffer.AsSpan(0, headerLen).SequenceEqual(headerBuffer));
        Assert.EndsWith($"${value.Length}\r\n", Encoding.ASCII.GetString(headerBuffer), StringComparison.Ordinal);
    }

    [Fact]
    public void Set_WithTtl_LengthMatchesWriter()
    {
        const string key = "cache:key:ttl";
        var value = "payload"u8.ToArray();

        var len = RedisRespProtocol.GetSetCommandLength(key, value.Length, ttlMs: 1500);
        var buffer = new byte[len];
        var written = RedisRespProtocol.WriteSetCommand(buffer, key, value, ttlMs: 1500);
        Assert.Equal(len, written);

        var text = Encoding.ASCII.GetString(buffer);
        Assert.StartsWith("*5\r\n", text, StringComparison.Ordinal);
        Assert.Contains("$3\r\nSET\r\n", text, StringComparison.Ordinal);
        Assert.Contains("$2\r\nPX\r\n", text, StringComparison.Ordinal);
        Assert.Contains("$4\r\n1500\r\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void MGet_LengthMatchesWriter()
    {
        var keys = new[] { "k1", "k2", "k3" };

        var len = RedisRespProtocol.GetMGetCommandLength(keys);
        var buffer = new byte[len];
        var written = RedisRespProtocol.WriteMGetCommand(buffer, keys);
        Assert.Equal(len, written);

        var text = Encoding.ASCII.GetString(buffer);
        Assert.StartsWith("*4\r\n", text, StringComparison.Ordinal);
        Assert.Contains("$4\r\nMGET\r\n", text, StringComparison.Ordinal);
        Assert.Contains("$2\r\nk1\r\n", text, StringComparison.Ordinal);
        Assert.Contains("$2\r\nk2\r\n", text, StringComparison.Ordinal);
        Assert.Contains("$2\r\nk3\r\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void MSet_LengthAndHeaderMatch()
    {
        var fullItems = new (string Key, ReadOnlyMemory<byte> Value)[]
        {
            ("k1", "v1"u8.ToArray()),
            ("k2", "value-two"u8.ToArray()),
            ("k3", "value-three"u8.ToArray()),
            ("k4", "value-four"u8.ToArray())
        };
        var headerItems = fullItems
            .Select(static item => (item.Key, item.Value.Length))
            .ToArray();

        var fullLen = RedisRespProtocol.GetMSetCommandLength(fullItems);
        var fullBuffer = new byte[fullLen];
        var fullWritten = RedisRespProtocol.WriteMSetCommand(fullBuffer, fullItems);
        Assert.Equal(fullLen, fullWritten);

        var headerLen = RedisRespProtocol.GetMSetHeaderLength(headerItems);
        var headerBuffer = new byte[headerLen];
        var headerWritten = RedisRespProtocol.WriteMSetCommandHeader(headerBuffer, headerItems);
        Assert.Equal(headerLen, headerWritten);

        var text = Encoding.ASCII.GetString(headerBuffer);
        Assert.StartsWith("*9\r\n", text, StringComparison.Ordinal);
        Assert.Contains("$4\r\nMSET\r\n", text, StringComparison.Ordinal);
        Assert.Contains("$2\r\n", text, StringComparison.Ordinal);
        Assert.Contains("$9\r\n", text, StringComparison.Ordinal);
        Assert.Contains("$11\r\n", text, StringComparison.Ordinal);
        Assert.Contains("$10\r\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("value-two", text, StringComparison.Ordinal);
    }

    private static void AssertSingleKeyCommand(
        string key,
        string expectedCommandBulk,
        Func<string, int> getLength,
        WriteSingleKeyCommand write)
    {
        var len = getLength(key);
        var buffer = new byte[len];
        var written = write(buffer, key);
        Assert.Equal(len, written);

        var text = Encoding.ASCII.GetString(buffer);
        Assert.StartsWith("*2\r\n", text, StringComparison.Ordinal);
        Assert.Contains(expectedCommandBulk, text, StringComparison.Ordinal);
        Assert.Contains($"${key.Length}\r\n{key}\r\n", text, StringComparison.Ordinal);
    }
}
