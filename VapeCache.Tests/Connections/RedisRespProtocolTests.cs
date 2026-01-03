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
}
