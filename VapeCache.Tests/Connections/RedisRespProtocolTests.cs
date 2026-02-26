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
}
